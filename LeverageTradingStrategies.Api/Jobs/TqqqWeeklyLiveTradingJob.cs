using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Helpers;
using LeverageTradingStrategies.Infrastructure.Quotes;
using LeverageTradingStrategies.Infrastructure.State;
using Microsoft.Extensions.Options;
using Quartz;

namespace LeverageTradingStrategies.Api.Jobs
{
    /// <summary>
    /// Quartz job driving the live TQQQ weekly strategy. Ticks on AppSettings:TqqqWeekly:
    /// CronSchedule (every few minutes during market hours by default) and, on each tick,
    /// calls into ITqqqWeeklyStrategyService at the appropriate phase(s) for the current time
    /// of day. Each phase is idempotent per trading date (guarded inside the strategy service
    /// itself via the state's LastXxxDate fields), so it's safe for this job to call every
    /// phase method unconditionally on every tick — only the phases whose gate conditions are
    /// met will actually do anything.
    /// </summary>
    [DisallowConcurrentExecution]
    public class TqqqWeeklyLiveTradingJob : IJob
    {
        private readonly IBroker _broker;
        private readonly IQuoteProvider _quoteProvider;
        private readonly ITqqqWeeklyStrategyService _strategy;
        private readonly ITqqqWeeklyStateStore _stateStore;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<TqqqWeeklyLiveTradingJob> _logger;

        public TqqqWeeklyLiveTradingJob(
            IBroker broker,
            IQuoteProvider quoteProvider,
            ITqqqWeeklyStrategyService strategy,
            ITqqqWeeklyStateStore stateStore,
            IOptions<AppSettingsOptions> options,
            ILogger<TqqqWeeklyLiveTradingJob> logger)
        {
            _broker = broker;
            _quoteProvider = quoteProvider;
            _strategy = strategy;
            _stateStore = stateStore;
            _options = options;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var ct = context.CancellationToken;
            var settings = _options.Value;
            var tqqq = settings.TqqqWeekly;

            if (!tqqq.Enabled)
            {
                _logger.LogDebug("TqqqWeeklyLiveTradingJob: disabled in config (AppSettings:TqqqWeekly:Enabled=false) — skipping tick");
                return;
            }

            string marketStatus;
            try
            {
                marketStatus = await _broker.GetEquityMarketStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TqqqWeeklyLiveTradingJob could not determine market status — skipping this tick");
                return;
            }

            if (marketStatus != "OPEN")
            {
                _logger.LogDebug("Market status is {Status} — skipping this tick", marketStatus);
                return;
            }

            var nowEastern = MarketHoursHelper.GetEasternNow();
            var tradingDate = DateOnly.FromDateTime(nowEastern);

            var quote = await _quoteProvider.GetQuoteAsync(tqqq.Symbol, ct);
            if (quote == null)
            {
                _logger.LogWarning("No quote available for {Symbol} — skipping this tick", tqqq.Symbol);
                return;
            }

            var accountNumber = settings.Trading.AccountNumber;
            decimal portfolioValue;
            try
            {
                portfolioValue = await _broker.GetPortfolioValueAsync(accountNumber, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not fetch portfolio value — skipping this tick");
                return;
            }

            if (portfolioValue <= 0)
            {
                _logger.LogWarning("Portfolio value reported as {Value} — skipping this tick to avoid a zero/garbage-sized order", portfolioValue);
                return;
            }

            var state = await _stateStore.GetOrCreateAsync(tqqq.Symbol, ct);

            // Holiday calendar limitation: these two flags approximate "day before last
            // trading day of the week" / "last trading day of the week" as plain
            // Thursday/Friday. A holiday-shortened week (e.g. Friday closed) is not handled
            // precisely — the EOW backstop in EvaluateSessionClose is the safety net for that
            // gap. See TQQQ_Weekly_Strategy_Spec_v1.md Section 7 for the documented risk.
            bool isDayBeforeLastTradingDayOfWeek = nowEastern.DayOfWeek == DayOfWeek.Thursday;
            bool isLastTradingDayOfWeek = nowEastern.DayOfWeek == DayOfWeek.Friday;
            bool isNearSessionClose = nowEastern.TimeOfDay >= new TimeSpan(15, 50, 0);

            await ExecuteDecisionAsync(
                _strategy.EvaluateSessionOpen(state, tradingDate, quote.OpenPrice, portfolioValue),
                accountNumber, tqqq.Symbol, ct);

            await ExecuteDecisionAsync(
                _strategy.EvaluateIntradayTakeProfit(state, quote.HighPrice),
                accountNumber, tqqq.Symbol, ct);

            if (nowEastern.Hour >= tqqq.ForceCloseHourEt)
            {
                await ExecuteDecisionAsync(
                    _strategy.EvaluateForceCloseWeekly(state, tradingDate, isDayBeforeLastTradingDayOfWeek, quote.LastPrice),
                    accountNumber, tqqq.Symbol, ct);
            }

            if (isNearSessionClose)
            {
                await ExecuteDecisionAsync(
                    _strategy.EvaluateSessionClose(state, tradingDate, isLastTradingDayOfWeek, quote.LastPrice),
                    accountNumber, tqqq.Symbol, ct);

                _strategy.RollDailyVolatilityHistory(state, tradingDate, quote.LastPrice);
            }

            await _stateStore.SaveAsync(state, ct);
        }

        private async Task ExecuteDecisionAsync(TqqqWeeklyDecision decision, string accountNumber, string symbol, CancellationToken ct)
        {
            if (decision.Action == TqqqWeeklyActionType.None)
            {
                _logger.LogDebug("No action: {Reason}", decision.Reason);
                return;
            }

            if (decision.Quantity <= 0)
            {
                _logger.LogWarning("{Action} decision for {Symbol} had non-positive quantity ({Qty}) — skipping order placement. Reason: {Reason}",
                    decision.Action, symbol, decision.Quantity, decision.Reason);
                return;
            }

            _logger.LogInformation("{Action} {Symbol} x{Qty} — {Reason}", decision.Action, symbol, decision.Quantity, decision.Reason);

            try
            {
                string result = decision.Action switch
                {
                    TqqqWeeklyActionType.EnterLong => await _broker.PlaceBuyMarketOrderAsync(accountNumber, symbol, decision.Quantity, ct),
                    TqqqWeeklyActionType.AddToPosition => await _broker.PlaceBuyMarketOrderAsync(accountNumber, symbol, decision.Quantity, ct),
                    TqqqWeeklyActionType.SellAll => await _broker.PlaceSellMarketOrderAsync(accountNumber, symbol, decision.Quantity, ct),
                    _ => "{}"
                };
                _logger.LogInformation("Broker response for {Action} {Symbol} x{Qty}: {Result}", decision.Action, symbol, decision.Quantity, result);
            }
            catch (Exception ex)
            {
                // NOTE (v1 known gap): this does not reconcile state against the broker's
                // actual fill on failure. If an order is rejected after state was already
                // mutated optimistically by the strategy service, state and the real broker
                // position can drift out of sync until the next manual reconciliation.
                // Recommended hardening before trading real size: add fill confirmation +
                // automatic reconciliation against IBroker.GetSymbolPositionAsync, similar to
                // the pattern used for options-seller order confirmation in MarketMatrixPreparer.
                _logger.LogError(ex, "Order placement FAILED for {Action} {Symbol} x{Qty} — state may now be out of sync with the real broker position, investigate immediately",
                    decision.Action, symbol, decision.Quantity);
            }
        }
    }
}
