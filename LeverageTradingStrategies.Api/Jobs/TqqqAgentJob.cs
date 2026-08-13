using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Helpers;
using LeverageTradingStrategies.Infrastructure.TqqqAgent;
using Microsoft.Extensions.Options;
using Quartz;

namespace LeverageTradingStrategies.Api.Jobs
{
    /// <summary>Quartz job for the TQQQ intraday discretionary agent -- see
    /// TQQQ_Intraday_Agent_Spec_v1.md at the repo root for the full design. Deliberately a
    /// standalone module (its own DB tables, its own broker-account wrapper, its own job) rather
    /// than reusing the generic StrategyInstances/IStrategyOrderExecutor framework
    /// TqqqWeeklyLiveTradingJob sits on -- that framework is hardwired to Schwab (IBroker), this
    /// module trades Tradier, and isolating blast radius from the other live-money jobs is
    /// deliberate given this is an experimental LLM-driven system trading real money from day one.
    ///
    /// One tick = one full decision cycle end to end: gather live portfolio + market context,
    /// ask Claude (a separate Anthropic API call, not this chat session -- see
    /// AnthropicDecisionClient) for a discretionary Hold/Buy/Sell, run the 9 deterministic hard
    /// checks (TqqqAgentValidatorService), place a real order if approved, persist the full
    /// audit row, and update the Redis short-term memory. Cadence (default every 5 minutes) is
    /// entirely controlled by this job's own Quartz cron trigger (AppSettings:TqqqAgent:
    /// CronSchedule) -- Execute() itself is a single pass, not a loop.</summary>
    [DisallowConcurrentExecution]
    public class TqqqAgentJob : IJob
    {
        private readonly ITqqqAgentBrokerService _broker;
        private readonly ITqqqAgentMarketDataService _marketData;
        private readonly ITqqqAgentMemoryService _memory;
        private readonly IAnthropicDecisionClient _claude;
        private readonly ITqqqAgentValidator _validator;
        private readonly ITqqqAgentSizingService _sizing;
        private readonly ITqqqAgentDecisionRepository _decisionRepository;
        private readonly ITqqqAgentStateRepository _stateRepository;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<TqqqAgentJob> _logger;

        public TqqqAgentJob(
            ITqqqAgentBrokerService broker,
            ITqqqAgentMarketDataService marketData,
            ITqqqAgentMemoryService memory,
            IAnthropicDecisionClient claude,
            ITqqqAgentValidator validator,
            ITqqqAgentSizingService sizing,
            ITqqqAgentDecisionRepository decisionRepository,
            ITqqqAgentStateRepository stateRepository,
            IOptions<AppSettingsOptions> options,
            ILogger<TqqqAgentJob> logger)
        {
            _broker = broker;
            _marketData = marketData;
            _memory = memory;
            _claude = claude;
            _validator = validator;
            _sizing = sizing;
            _decisionRepository = decisionRepository;
            _stateRepository = stateRepository;
            _options = options;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var ct = context.CancellationToken;
            var cfg = _options.Value.TqqqAgent;

            if (!cfg.Enabled)
            {
                _logger.LogDebug("TqqqAgentJob: disabled in config (AppSettings:TqqqAgent:Enabled=false) — skipping tick");
                return;
            }

            var controlState = await _stateRepository.GetControlStateAsync(ct);
            if (controlState.IsKilled)
            {
                _logger.LogDebug("TqqqAgentJob: kill switch active ({Reason}) — skipping tick entirely", controlState.Reason);
                return;
            }

            var validatorConfig = BuildValidatorConfig(cfg);
            var nowEastern = MarketHoursHelper.GetEasternNow();
            var timeOfDay = nowEastern.TimeOfDay;
            var marketOpen = new TimeSpan(cfg.MarketOpenHourEt, cfg.MarketOpenMinuteEt, 0);
            var marketClose = new TimeSpan(cfg.MarketCloseHourEt, cfg.MarketCloseMinuteEt, 0);
            var forceFlatten = new TimeSpan(cfg.ForceFlattenHourEt, cfg.ForceFlattenMinuteEt, 0);

            if (timeOfDay < marketOpen || timeOfDay >= marketClose)
            {
                _logger.LogDebug("TqqqAgentJob: outside market hours ({Time} ET) — skipping tick", timeOfDay);
                return;
            }

            try
            {
                await RunCycleAsync(cfg, validatorConfig, controlState.IsPaused, nowEastern, forceFlatten, ct);
            }
            catch (Exception ex)
            {
                // A failed cycle should never crash the job host or block future ticks -- worst
                // case this cycle just does nothing (no trade), which is always the safe outcome.
                _logger.LogError(ex, "TqqqAgentJob: cycle failed unexpectedly — no action taken this tick");
            }
        }

        private async Task RunCycleAsync(
            TqqqAgentOptions cfg,
            TqqqAgentValidatorConfig validatorConfig,
            bool isPaused,
            DateTime nowEastern,
            TimeSpan forceFlatten,
            CancellationToken ct)
        {
            var tradeDateEt = nowEastern.ToString("yyyy-MM-dd");
            var (todayStartUtc, todayEndUtc) = GetTodayCycleRangeUtc(nowEastern);
            var todaysDecisions = await _decisionRepository.GetByCycleRangeAsync(todayStartUtc, todayEndUtc, ct);
            var (realizedPnLToday, consecutiveLosses) = SummarizeToday(todaysDecisions);

            var dayStartEquity = await _stateRepository.GetDayStartEquityAsync(tradeDateEt, ct);

            var haltActive = consecutiveLosses >= validatorConfig.ConsecutiveLossLimit;
            string? haltReason = haltActive ? $"{consecutiveLosses} consecutive losses today — new entries halted." : null;

            // Portfolio needs live equity regardless of whether today's day-start row exists yet
            // (first cycle of the day) or not, so fetch it once and seed the row here if absent.
            var portfolio = await _broker.GetPortfolioSnapshotAsync(
                dayStartEquity ?? 0m, realizedPnLToday, consecutiveLosses, haltActive, haltReason, ct);

            if (dayStartEquity == null)
            {
                await _stateRepository.SetDayStartEquityIfAbsentAsync(tradeDateEt, portfolio.TotalEquity, ct);
                portfolio.DayStartEquity = portfolio.TotalEquity;
            }

            if (!haltActive && portfolio.DayStartEquity > 0)
            {
                var dailyLossThreshold = -Math.Abs(validatorConfig.DailyLossStopPct) * portfolio.DayStartEquity;
                if (portfolio.RealizedPnLToday <= dailyLossThreshold)
                {
                    portfolio.HaltActive = true;
                    portfolio.HaltReason = $"Daily loss stop hit ({portfolio.RealizedPnLToday:C} vs {dailyLossThreshold:C} threshold) — new entries halted.";
                }
            }

            var market = await _marketData.GetSnapshotAsync(nowEastern, new TimeSpan(cfg.MarketOpenHourEt, cfg.MarketOpenMinuteEt, 0), forceFlatten, ct);
            var recentDecisions = await _memory.GetRecentAsync(cfg.RecentDecisionsShownToClaude, ct);

            var rawDecision = await _claude.GetDecisionAsync(portfolio, market, recentDecisions, ct);

            // Paused = no NEW entries, but an existing position still gets fully managed
            // (including the forced end-of-day flatten below, which the validator itself
            // enforces regardless of this override). Not one of the validator's 9 numbered
            // checks -- this is job-level, since "paused" is an operator control state, not a
            // per-decision risk rule.
            if (isPaused && rawDecision.Action == TqqqAgentAction.Buy && !portfolio.Holding)
            {
                _logger.LogDebug("TqqqAgentJob: paused — overriding Claude's Buy decision to Hold (no new entries while paused)");
                rawDecision = new TqqqAgentRawDecision { Action = TqqqAgentAction.Hold, Confidence = rawDecision.Confidence, Why = $"[Paused override] {rawDecision.Why}" };
            }

            var proposedBuyShares = rawDecision.Action == TqqqAgentAction.Buy
                ? _sizing.ComputeBuyShares(portfolio.CashAvailable, market.LastPrice, cfg.EquityUsageFraction, cfg.MaxNotionalCeiling)
                : 0;

            var result = _validator.Validate(rawDecision, portfolio, nowEastern, killSwitchActive: false, proposedBuyShares, validatorConfig);

            var decisionId = await _decisionRepository.InsertAsync(new TqqqAgentDecisionRecord
            {
                CycleUtc = DateTime.UtcNow,
                PortfolioSnapshotJson = System.Text.Json.JsonSerializer.Serialize(portfolio),
                MarketSnapshotJson = System.Text.Json.JsonSerializer.Serialize(market),
                RawAction = rawDecision.Action,
                RawConfidence = rawDecision.Confidence,
                RawWhy = rawDecision.Why,
                Approved = result.Approved,
                FinalAction = result.FinalAction,
                Shares = result.Shares,
                RejectReason = result.RejectReason,
                OrderStatus = "None"
            }, ct);

            decimal? realizedPnLThisTrade = null;
            var orderStatus = "None";
            string? brokerOrderId = null;
            decimal? fillPrice = null;
            string? errorMessage = null;

            if (result.Approved && result.FinalAction != TqqqAgentAction.Hold && result.Shares > 0)
            {
                var side = result.FinalAction == TqqqAgentAction.Buy ? "buy" : "sell";
                var orderResult = await _broker.PlaceOrderAsync(side, result.Shares, ct);

                orderStatus = orderResult.Status;
                brokerOrderId = orderResult.BrokerOrderId;
                fillPrice = orderResult.FillPrice;
                errorMessage = orderResult.ErrorMessage;

                if (orderResult.Success && orderResult.Status == "Filled" && result.FinalAction == TqqqAgentAction.Sell && portfolio.EntryPrice.HasValue && orderResult.FillPrice.HasValue)
                {
                    realizedPnLThisTrade = (orderResult.FillPrice.Value - portfolio.EntryPrice.Value) * orderResult.FilledQuantity;
                }

                await _decisionRepository.UpdateOrderResultAsync(decisionId, orderStatus, brokerOrderId, fillPrice, realizedPnLThisTrade, errorMessage, ct);

                if (!orderResult.Success)
                    _logger.LogError("TqqqAgentJob: order failed for {Action} {Shares} TQQQ: {Error}", result.FinalAction, result.Shares, errorMessage);
                else
                    _logger.LogInformation("TqqqAgentJob: {Action} {Shares} TQQQ — {Status} (order {OrderId}, fill {Fill})", result.FinalAction, result.Shares, orderStatus, brokerOrderId, fillPrice);
            }
            else if (!result.Approved)
            {
                _logger.LogInformation("TqqqAgentJob: decision not approved (check {Check}): {Reason}", result.FailedCheckNumber, result.RejectReason);
            }

            await _memory.AppendAsync(new TqqqAgentRecentDecision
            {
                TimestampUtc = DateTime.UtcNow,
                Action = result.FinalAction,
                Confidence = rawDecision.Confidence,
                Why = rawDecision.Why,
                WasExecuted = orderStatus == "Filled",
                RejectReason = result.RejectReason,
                RealizedPnL = realizedPnLThisTrade
            }, cfg.MaxRecentDecisionsKept, ct);
        }

        private static (decimal RealizedPnLToday, int ConsecutiveLosses) SummarizeToday(List<TqqqAgentDecisionRecord> todaysDecisions)
        {
            var executedSells = todaysDecisions.Where(d => d.RealizedPnL.HasValue).ToList(); // ascending by CycleUtc
            var realizedPnLToday = executedSells.Sum(d => d.RealizedPnL!.Value);

            var consecutiveLosses = 0;
            for (var i = executedSells.Count - 1; i >= 0; i--)
            {
                if (executedSells[i].RealizedPnL!.Value < 0)
                    consecutiveLosses++;
                else
                    break;
            }
            return (realizedPnLToday, consecutiveLosses);
        }

        private static (DateTime StartUtc, DateTime EndUtc) GetTodayCycleRangeUtc(DateTime nowEastern)
        {
            var todayStartEt = nowEastern.Date;
            var tomorrowStartEt = todayStartEt.AddDays(1);
            return (MarketHoursHelper.EasternToUtc(todayStartEt), MarketHoursHelper.EasternToUtc(tomorrowStartEt));
        }

        private static TqqqAgentValidatorConfig BuildValidatorConfig(TqqqAgentOptions cfg) => new()
        {
            ConsecutiveLossLimit = cfg.ConsecutiveLossLimit,
            DailyLossStopPct = cfg.DailyLossStopPct,
            MarketOpenHourEt = cfg.MarketOpenHourEt,
            MarketOpenMinuteEt = cfg.MarketOpenMinuteEt,
            MarketCloseHourEt = cfg.MarketCloseHourEt,
            MarketCloseMinuteEt = cfg.MarketCloseMinuteEt,
            EntryWindowStartHourEt = cfg.EntryWindowStartHourEt,
            EntryWindowStartMinuteEt = cfg.EntryWindowStartMinuteEt,
            EntryWindowEndHourEt = cfg.EntryWindowEndHourEt,
            EntryWindowEndMinuteEt = cfg.EntryWindowEndMinuteEt,
            ForceFlattenHourEt = cfg.ForceFlattenHourEt,
            ForceFlattenMinuteEt = cfg.ForceFlattenMinuteEt,
            MinConfidenceToAct = cfg.MinConfidenceToAct
        };
    }
}
