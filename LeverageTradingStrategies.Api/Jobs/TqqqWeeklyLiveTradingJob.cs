using LeverageTradingStrategies.Domain.Orders;
using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
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
    ///
    /// Capital sizing now comes from this strategy INSTANCE's own StrategyInstances.CurrentCapital
    /// (not raw broker account equity), and every tick is gated on the instance's Status:
    ///   - Killed: skip everything (the kill controller endpoint already flattened the position
    ///     synchronously when the switch was thrown — this job just stays out of the way).
    ///   - Paused: skip EvaluateSessionOpen ONLY when flat (no new entries), but keep running
    ///     every other phase normally so an existing position is still fully managed (take-profit,
    ///     avg-down via SessionOpen when holding, force-close, stop-loss) until it's closed out.
    ///   - Running: normal, unrestricted operation.
    /// </summary>
    [DisallowConcurrentExecution]
    public class TqqqWeeklyLiveTradingJob : IJob
    {
        private const string StrategyType = "TqqqWeekly";

        private readonly IBroker _broker;
        private readonly IQuoteProvider _quoteProvider;
        private readonly ITqqqWeeklyStrategyService _strategy;
        private readonly ITqqqWeeklyStateStore _stateStore;
        private readonly IStrategyInstanceRepository _instanceRepository;
        private readonly IStrategyOrderExecutor _orderExecutor;
        private readonly ITqqqWeeklyConfigProvider _configProvider;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<TqqqWeeklyLiveTradingJob> _logger;

        public TqqqWeeklyLiveTradingJob(
            IBroker broker,
            IQuoteProvider quoteProvider,
            ITqqqWeeklyStrategyService strategy,
            ITqqqWeeklyStateStore stateStore,
            IStrategyInstanceRepository instanceRepository,
            IStrategyOrderExecutor orderExecutor,
            ITqqqWeeklyConfigProvider configProvider,
            IOptions<AppSettingsOptions> options,
            ILogger<TqqqWeeklyLiveTradingJob> logger)
        {
            _broker = broker;
            _quoteProvider = quoteProvider;
            _strategy = strategy;
            _stateStore = stateStore;
            _instanceRepository = instanceRepository;
            _orderExecutor = orderExecutor;
            _configProvider = configProvider;
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

            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            if (instance.Status == StrategyStatus.Killed)
            {
                _logger.LogDebug("Strategy instance #{Id} ({Symbol}) is Killed — skipping this tick entirely", instance.Id, tqqq.Symbol);
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

            if (instance.CurrentCapital <= 0)
            {
                _logger.LogWarning("Strategy instance #{Id} ({Symbol}) has CurrentCapital {Value} — skipping this tick to avoid a zero/garbage-sized order",
                    instance.Id, tqqq.Symbol, instance.CurrentCapital);
                return;
            }

            var accountNumber = settings.Trading.AccountNumber;
            var isSimulated = settings.Trading.UseSimulatedBroker;
            var state = await _stateStore.GetOrCreateAsync(instance.Id, tqqq.Symbol, ct);
            // Resolved fresh every tick from the StrategyConfig DB table (seeded from
            // appsettings on first run) -- so a tuning change made directly in the DB takes
            // effect on the very next tick, no app restart required.
            var config = await _configProvider.GetAsync(instance.Id, ct);

            // Holiday calendar limitation: these two flags approximate "day before last
            // trading day of the week" / "last trading day of the week" as plain
            // Thursday/Friday. A holiday-shortened week (e.g. Friday closed) is not handled
            // precisely — the EOW backstop in EvaluateSessionClose is the safety net for that
            // gap. See TQQQ_Weekly_Strategy_Spec_v1.md Section 7 for the documented risk.
            bool isDayBeforeLastTradingDayOfWeek = nowEastern.DayOfWeek == DayOfWeek.Thursday;
            bool isLastTradingDayOfWeek = nowEastern.DayOfWeek == DayOfWeek.Friday;
            bool isNearSessionClose = nowEastern.TimeOfDay >= new TimeSpan(15, 50, 0);

            // Pause = no NEW entries, but an existing position keeps being fully managed.
            // Skip only when paused AND currently flat; every other phase below (take-profit,
            // avg-down-while-holding, force-close, stop-loss) still runs regardless of pause.
            bool skipSessionOpen = instance.Status == StrategyStatus.Paused && !state.Holding;
            if (skipSessionOpen)
            {
                _logger.LogDebug("Strategy instance #{Id} ({Symbol}) is Paused and flat — skipping new-entry evaluation this tick", instance.Id, tqqq.Symbol);
            }
            else
            {
                await _orderExecutor.ExecuteAsync(
                    instance,
                    _strategy.EvaluateSessionOpen(config, state, tradingDate, quote.OpenPrice, instance.CurrentCapital),
                    quote.OpenPrice, accountNumber, isSimulated, ct);
            }

            await _orderExecutor.ExecuteAsync(
                instance,
                _strategy.EvaluateIntradayTakeProfit(state, quote.HighPrice),
                quote.HighPrice, accountNumber, isSimulated, ct);

            if (nowEastern.Hour >= config.ForceCloseHourEt)
            {
                await _orderExecutor.ExecuteAsync(
                    instance,
                    _strategy.EvaluateForceCloseWeekly(config, state, tradingDate, isDayBeforeLastTradingDayOfWeek, quote.LastPrice),
                    quote.LastPrice, accountNumber, isSimulated, ct);
            }

            if (isNearSessionClose)
            {
                await _orderExecutor.ExecuteAsync(
                    instance,
                    _strategy.EvaluateSessionClose(config, state, tradingDate, isLastTradingDayOfWeek, quote.LastPrice),
                    quote.LastPrice, accountNumber, isSimulated, ct);

                _strategy.RollDailyVolatilityHistory(config, state, tradingDate, quote.LastPrice);
            }

            await _stateStore.SaveAsync(instance.Id, state, ct);
        }
    }
}
