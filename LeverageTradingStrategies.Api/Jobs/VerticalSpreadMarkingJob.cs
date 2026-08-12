using LeverageTradingStrategies.Domain.Options;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
using LeverageTradingStrategies.Infrastructure.Options;
using LeverageTradingStrategies.Infrastructure.Quotes;
using Microsoft.Extensions.Options;
using Quartz;

namespace LeverageTradingStrategies.Api.Jobs
{
    /// <summary>
    /// Quartz job that keeps every Paper/Live vertical spread's mark-to-market up to date — this
    /// IS the "track it with real data so I know the performance of the paper strategy" piece:
    /// a Paper strategy is never touched by a broker call, but every tick it still gets a fresh
    /// Tradier chain lookup, a real mark-to-market P&amp;L snapshot (VerticalSpreadMarks), and gets
    /// auto-settled at expiration exactly like a Live one would (Paper settles against intrinsic
    /// value at expiration since there's no broker fill to read; Live places the real close combo
    /// order).
    ///
    /// Known v1 simplification: closes on expiration DAY as soon as this job ticks, rather than
    /// waiting for a specific "just before the closing bell" window like OptionsSellerStrategyService
    /// does — acceptable for now given weekly/monthly cadences, but worth tightening if 0DTE/short-
    /// dated spreads are ever added here.
    /// </summary>
    [DisallowConcurrentExecution]
    public class VerticalSpreadMarkingJob : IJob
    {
        private readonly IVerticalSpreadRepository _repository;
        private readonly ITradierOptionsProvider _optionsProvider;
        private readonly SchwabQuoteProvider _quoteProvider;
        private readonly IVerticalSpreadPricingService _pricingService;
        private readonly IVerticalSpreadOrderExecutor _orderExecutor;
        private readonly SchwabBroker _schwabBroker;
        private readonly SimulatedBroker _simulatedBroker;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<VerticalSpreadMarkingJob> _logger;

        // Deliberately takes the CONCRETE SchwabQuoteProvider, not the DI-resolved
        // IQuoteProvider -- a vertical spread's whole point is to track REAL market data even
        // while Paper, independent of AppSettings:Trading:UseSimulatedBroker (which the TQQQ
        // weekly job's dry-run toggle controls, and which resolves IQuoteProvider to
        // SimulatedBroker with no seeded quotes at all). Same reasoning as BrokerTestController.
        public VerticalSpreadMarkingJob(
            IVerticalSpreadRepository repository,
            ITradierOptionsProvider optionsProvider,
            SchwabQuoteProvider quoteProvider,
            IVerticalSpreadPricingService pricingService,
            IVerticalSpreadOrderExecutor orderExecutor,
            SchwabBroker schwabBroker,
            SimulatedBroker simulatedBroker,
            IOptions<AppSettingsOptions> options,
            ILogger<VerticalSpreadMarkingJob> logger)
        {
            _repository = repository;
            _optionsProvider = optionsProvider;
            _quoteProvider = quoteProvider;
            _pricingService = pricingService;
            _orderExecutor = orderExecutor;
            _schwabBroker = schwabBroker;
            _simulatedBroker = simulatedBroker;
            _options = options;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var ct = context.CancellationToken;
            var settings = _options.Value;

            if (!settings.VerticalSpread.Enabled)
            {
                _logger.LogDebug("VerticalSpreadMarkingJob: disabled in config — skipping tick");
                return;
            }

            var strategies = await _repository.GetActiveAsync(ct);
            if (strategies.Count == 0)
                return;

            var nowUtc = DateTime.UtcNow;
            var todayEastern = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, GetEasternTimeZone()).Date;

            // Group by (Symbol, ExpirationDate) so strategies sharing a chain don't re-fetch it.
            foreach (var group in strategies.GroupBy(s => (s.Symbol, s.ExpirationDate.Date)))
            {
                OptionChainDto chain;
                try
                {
                    chain = await _optionsProvider.GetOptionChainAsync(group.Key.Symbol, group.Key.Date, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "VerticalSpreadMarkingJob: chain fetch failed for {Symbol} exp {Exp:yyyy-MM-dd} — skipping this group this tick", group.Key.Symbol, group.Key.Date);
                    continue;
                }

                var quote = await _quoteProvider.GetQuoteAsync(group.Key.Symbol, ct);
                if (quote == null || quote.LastPrice <= 0)
                {
                    _logger.LogWarning("VerticalSpreadMarkingJob: no usable underlying quote for {Symbol} — skipping this group this tick", group.Key.Symbol);
                    continue;
                }

                foreach (var strategy in group)
                {
                    await ProcessStrategyAsync(strategy, chain, quote.LastPrice, todayEastern, settings, ct);
                }
            }
        }

        private async Task ProcessStrategyAsync(
            VerticalSpreadStrategyRecord strategy, OptionChainDto chain, decimal underlyingPrice,
            DateTime todayEastern, AppSettingsOptions settings, CancellationToken ct)
        {
            var shortLeg = chain.Options.FirstOrDefault(o => o.Symbol == strategy.ShortOptionSymbol);
            var longLeg = chain.Options.FirstOrDefault(o => o.Symbol == strategy.LongOptionSymbol);

            if (shortLeg == null || longLeg == null)
            {
                _logger.LogWarning("VerticalSpreadMarkingJob: strategy #{Id} legs not found in the {Symbol} exp {Exp:yyyy-MM-dd} chain — skipping mark this tick",
                    strategy.Id, strategy.Symbol, strategy.ExpirationDate);
                return;
            }

            var (spreadMarkPrice, unrealizedPnL, netDelta) = _pricingService.ComputeMark(
                shortLeg, longLeg, strategy.NetCreditReceived ?? strategy.NetCreditAtBuild, strategy.Contracts);

            int dte = Math.Max(0, (strategy.ExpirationDate.Date - todayEastern).Days);

            await _repository.InsertMarkAsync(new VerticalSpreadMarkRecord
            {
                VerticalSpreadStrategyId = strategy.Id,
                MarkUtc = DateTime.UtcNow,
                UnderlyingPrice = underlyingPrice,
                ShortMid = shortLeg.Mid,
                LongMid = longLeg.Mid,
                SpreadMarkPrice = spreadMarkPrice,
                UnrealizedPnL = unrealizedPnL,
                ShortDelta = shortLeg.Delta,
                LongDelta = longLeg.Delta,
                NetDelta = netDelta,
                DaysToExpiration = dte
            }, ct);

            bool expired = todayEastern >= strategy.ExpirationDate.Date;
            if (!expired)
                return;

            _logger.LogInformation("VerticalSpreadMarkingJob: strategy #{Id} ({Symbol}) has reached its expiration date {Exp:yyyy-MM-dd} — auto-closing", strategy.Id, strategy.Symbol, strategy.ExpirationDate);

            // A Paper strategy's own Status (not the app-wide UseSimulatedBroker toggle) is what
            // decides real-vs-simulated here -- a Live vertical spread must really be closed at
            // expiration regardless of what the TQQQ weekly job's dry-run switch is set to.
            // (Paper closes never actually reach the broker either way -- CloseAsync
            // short-circuits to a pure settlement for Status==Paper.)
            var broker = strategy.Status == VerticalSpreadStatus.Live ? (IBroker)_schwabBroker : _simulatedBroker;
            await _orderExecutor.CloseAsync(strategy, broker, settings.Trading.AccountNumber, "Expiration reached — auto-closed", spreadMarkPrice, ct);
        }

        private static TimeZoneInfo GetEasternTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } // Windows ID fallback
        }
    }
}
