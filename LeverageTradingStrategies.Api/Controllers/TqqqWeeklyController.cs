using LeverageTradingStrategies.Domain.Orders;
using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
using LeverageTradingStrategies.Infrastructure.Models;
using LeverageTradingStrategies.Infrastructure.Quotes;
using LeverageTradingStrategies.Infrastructure.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeverageTradingStrategies.Api.Controllers
{
    /// <summary>Status, order history, and kill/pause/resume controls for the TQQQ weekly
    /// strategy instance. Backs the monitoring dashboard. All state-changing endpoints act on
    /// the single (StrategyType="TqqqWeekly", Symbol=config.Symbol) instance — multi-instance
    /// support (e.g. running the same strategy on two symbols at once) would need an
    /// instanceId route parameter, not built here since only one TQQQ instance is in scope.</summary>
    [ApiController]
    [Route("api/tqqq-weekly")]
    public class TqqqWeeklyController : ControllerBase
    {
        private const string StrategyType = "TqqqWeekly";

        private readonly ITqqqWeeklyStateStore _stateStore;
        private readonly ITqqqWeeklyStrategyService _strategy;
        private readonly IStrategyInstanceRepository _instanceRepository;
        private readonly IStrategyOrderRepository _orderRepository;
        private readonly IStrategyOrderExecutor _orderExecutor;
        private readonly IStrategyConfigRepository _configRepository;
        private readonly ITqqqWeeklyConfigProvider _configProvider;
        private readonly IQuoteProvider _quoteProvider;
        private readonly IBroker _broker;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<TqqqWeeklyController> _logger;

        public TqqqWeeklyController(
            ITqqqWeeklyStateStore stateStore,
            ITqqqWeeklyStrategyService strategy,
            IStrategyInstanceRepository instanceRepository,
            IStrategyOrderRepository orderRepository,
            IStrategyOrderExecutor orderExecutor,
            IStrategyConfigRepository configRepository,
            ITqqqWeeklyConfigProvider configProvider,
            IQuoteProvider quoteProvider,
            IBroker broker,
            IOptions<AppSettingsOptions> options,
            ILogger<TqqqWeeklyController> logger)
        {
            _stateStore = stateStore;
            _strategy = strategy;
            _instanceRepository = instanceRepository;
            _orderRepository = orderRepository;
            _orderExecutor = orderExecutor;
            _configRepository = configRepository;
            _configProvider = configProvider;
            _quoteProvider = quoteProvider;
            _broker = broker;
            _options = options;
            _logger = logger;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);
            var state = await _stateStore.GetOrCreateAsync(instance.Id, tqqq.Symbol, ct);
            var runtimeConfig = await _configProvider.GetAsync(instance.Id, ct);
            return Ok(new
            {
                config = new
                {
                    tqqq.Enabled,
                    tqqq.Symbol,
                    tqqq.CronSchedule,
                    useSimulatedBroker = _options.Value.Trading.UseSimulatedBroker
                },
                // DB-backed (StrategyConfig table) tuning parameters -- source of truth after
                // the instance's first-ever tick, NOT what's currently in appsettings.json.
                runtimeConfig,
                instance = new
                {
                    instance.Id,
                    instance.StrategyType,
                    instance.Symbol,
                    instance.AllocatedCapital,
                    instance.CompoundingEnabled,
                    instance.CurrentCapital,
                    Status = instance.Status.ToString(),
                    instance.CreatedUtc,
                    instance.UpdatedUtc
                },
                state
            });
        }

        /// <summary>Current DB-backed tuning parameters for this instance (seeded from
        /// appsettings.json on first run, editable after that via POST config).</summary>
        [HttpGet("config")]
        public async Task<IActionResult> GetConfig(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);
            var runtimeConfig = await _configProvider.GetAsync(instance.Id, ct);
            return Ok(runtimeConfig);
        }

        /// <summary>Sets a single tuning parameter in the StrategyConfig DB table. Takes
        /// effect on the live job's very next tick -- no app restart. Value is passed as a
        /// plain string (e.g. "-0.20", "true", "14") and parsed the same way the config
        /// provider parses it when resolving the runtime config.</summary>
        [HttpPost("config")]
        public async Task<IActionResult> SetConfig([FromBody] SetTqqqWeeklyConfigRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Key) || !TqqqWeeklyConfigProvider.KnownKeys.Contains(request.Key))
            {
                return BadRequest(new
                {
                    message = $"Unknown config key '{request.Key}'.",
                    knownKeys = TqqqWeeklyConfigProvider.KnownKeys
                });
            }
            if (request.Value is null)
            {
                return BadRequest(new { message = "Value is required." });
            }

            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);
            await _configRepository.SetAsync(instance.Id, request.Key, request.Value, ct);
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) config changed via API: {Key} = {Value}", instance.Id, instance.Symbol, request.Key, request.Value);

            var runtimeConfig = await _configProvider.GetAsync(instance.Id, ct);
            return Ok(runtimeConfig);
        }

        [HttpGet("orders")]
        public async Task<IActionResult> GetOrders([FromQuery] int limit, CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);
            var effectiveLimit = limit <= 0 ? 50 : Math.Min(limit, 500);
            var orders = await _orderRepository.GetRecentAsync(instance.Id, effectiveLimit, ct);
            return Ok(orders);
        }

        /// <summary>Pause = no new entries; an existing position keeps being fully managed
        /// (take-profit, avg-down, force-close, stop-loss) until it closes out on its own.</summary>
        [HttpPost("pause")]
        public async Task<IActionResult> Pause(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Paused, ct);
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) PAUSED via API — no new entries until resumed", instance.Id, instance.Symbol);
            return Ok(new { instance.Id, instance.Symbol, Status = StrategyStatus.Paused.ToString() });
        }

        /// <summary>Resume works from Paused OR Killed — Kill is a strong stop, not a
        /// one-way door: if a kill attempt ever leaves things in a state you want to walk
        /// back from (or the position has since been dealt with manually), this re-enables
        /// normal trading. It does NOT re-open any position on its own.</summary>
        [HttpPost("resume")]
        public async Task<IActionResult> Resume(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Running, ct);
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) RESUMED via API (was {PreviousStatus})", instance.Id, instance.Symbol, instance.Status);
            return Ok(new { instance.Id, instance.Symbol, Status = StrategyStatus.Running.ToString() });
        }

        /// <summary>Broker-confirmed snapshot of what a Kill would do right now, for the
        /// dashboard to show the user before they confirm. Read-only -- does not touch status
        /// or place any order.</summary>
        [HttpGet("kill-preview")]
        public async Task<IActionResult> KillPreview(CancellationToken ct)
        {
            var settings = _options.Value;
            var tqqq = settings.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            SymbolPositionInfo? brokerPosition;
            try
            {
                brokerPosition = await _broker.GetSymbolPositionAsync(settings.Trading.AccountNumber, tqqq.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kill preview: could not fetch broker position for {Symbol}", tqqq.Symbol);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not reach the broker to confirm the current position. Try again before killing." });
            }

            var quote = await _quoteProvider.GetQuoteAsync(tqqq.Symbol, ct);
            int brokerQty = (int)(brokerPosition?.LongQuantity ?? 0m);

            return Ok(new
            {
                instance.Id,
                instance.Symbol,
                Status = instance.Status.ToString(),
                alreadyKilled = instance.Status == StrategyStatus.Killed,
                brokerQuantity = brokerQty,
                brokerAveragePrice = brokerPosition?.AveragePrice,
                brokerMarketValue = brokerPosition?.MarketValue,
                quoteAvailable = quote != null,
                currentPrice = quote?.LastPrice,
                willSquareOff = brokerQty > 0,
                message = brokerQty > 0
                    ? $"Broker shows {brokerQty} share(s) of {tqqq.Symbol} open — killing will sell all {brokerQty} at market and permanently stop new entries until Resumed."
                    : "Broker shows no open position for this symbol — killing will just stop new entries (nothing to sell)."
            });
        }

        /// <summary>Immediate, synchronous square-off. Safe-by-design: the instance is only
        /// ever marked Killed AFTER the position is confirmed squared off (or confirmed
        /// already flat) directly against the broker -- if anything along the way fails (no
        /// broker connectivity, no quote, the sell order itself gets rejected), the instance
        /// is left Paused (no new entries, but NOT permanently stopped) instead of Killed, so
        /// it's always safe to retry Kill or Resume normal trading. Uses the same
        /// IStrategyOrderExecutor the live job uses, so a real square-off order produces an
        /// identically-shaped audit row in StrategyOrders (real or simulated depending on
        /// AppSettings:Trading:UseSimulatedBroker). Does not wait for the next Quartz tick.</summary>
        [HttpPost("kill")]
        public async Task<IActionResult> Kill(CancellationToken ct)
        {
            var settings = _options.Value;
            var tqqq = settings.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            if (instance.Status == StrategyStatus.Killed)
                return Ok(new { instance.Id, instance.Symbol, Status = StrategyStatus.Killed.ToString(), message = "Already Killed — no-op." });

            // Pause FIRST -- blocks new entries from a concurrently-running job tick while we
            // work, AND is the safe fallback state if anything below fails: unlike Killed,
            // Paused can still be Resumed, so a kill attempt that doesn't fully complete never
            // strands the instance.
            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Paused, ct);
            instance.Status = StrategyStatus.Paused;

            SymbolPositionInfo? brokerPosition;
            try
            {
                brokerPosition = await _broker.GetSymbolPositionAsync(settings.Trading.AccountNumber, tqqq.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kill switch: could not confirm current position with the broker for {Symbol} — kill NOT completed, instance left Paused", tqqq.Symbol);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    instance.Id,
                    instance.Symbol,
                    Status = StrategyStatus.Paused.ToString(),
                    message = "Could not confirm the current position with the broker — kill was NOT completed. The instance is Paused (no new entries) but not Killed. Fix connectivity and retry Kill, or Resume to keep trading."
                });
            }

            int brokerQty = (int)(brokerPosition?.LongQuantity ?? 0m);
            var state = await _stateStore.GetOrCreateAsync(instance.Id, tqqq.Symbol, ct);

            if (brokerQty <= 0)
            {
                // Broker confirms flat -- nothing to square off. Reconcile local state if it
                // disagreed, then it's safe to mark Killed (no stranded position possible).
                if (state.Holding)
                {
                    _logger.LogWarning("Kill switch: broker reports flat for {Symbol} but local state showed a position — reconciling local state to flat", tqqq.Symbol);
                }
                var flatDecision = _strategy.EvaluateKillSwitch(state, 0m, brokerConfirmedQuantity: 0);
                await _stateStore.SaveAsync(instance.Id, state, ct);
                await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Killed, ct);
                _logger.LogWarning("Strategy instance #{Id} ({Symbol}) KILLED via API — no open position at broker", instance.Id, instance.Symbol);
                return Ok(new
                {
                    instance.Id,
                    instance.Symbol,
                    Status = StrategyStatus.Killed.ToString(),
                    message = "Broker confirmed no open position — nothing to square off. Instance Killed.",
                    squareOff = new { flatDecision.Action, flatDecision.Quantity, flatDecision.Reason }
                });
            }

            var quote = await _quoteProvider.GetQuoteAsync(tqqq.Symbol, ct);
            if (quote == null)
            {
                _logger.LogError("Kill switch: broker shows {Qty} open share(s) of {Symbol} but no quote is available to price the square-off — kill NOT completed, instance left Paused", brokerQty, tqqq.Symbol);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    instance.Id,
                    instance.Symbol,
                    Status = StrategyStatus.Paused.ToString(),
                    openPosition = new { quantity = brokerQty, averagePrice = brokerPosition!.AveragePrice },
                    message = $"Broker shows {brokerQty} share(s) of {tqqq.Symbol} still open, but no quote is available to square it off — kill was NOT completed. The instance is Paused; retry Kill once quotes are available."
                });
            }

            var decision = _strategy.EvaluateKillSwitch(state, quote.LastPrice, brokerConfirmedQuantity: brokerQty);
            bool success = await _orderExecutor.ExecuteAsync(instance, decision, quote.LastPrice, settings.Trading.AccountNumber, settings.Trading.UseSimulatedBroker, ct);
            await _stateStore.SaveAsync(instance.Id, state, ct);

            if (!success)
            {
                _logger.LogError("Kill switch: square-off order FAILED for {Symbol} — kill NOT completed, instance left Paused so it can be retried or resumed", tqqq.Symbol);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    instance.Id,
                    instance.Symbol,
                    Status = StrategyStatus.Paused.ToString(),
                    message = "The square-off order failed at the broker — kill was NOT completed. Check the order log for details, then retry Kill (or Resume to keep trading with the position still open)."
                });
            }

            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Killed, ct);
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) KILLED via API — position squared off", instance.Id, instance.Symbol);
            return Ok(new
            {
                instance.Id,
                instance.Symbol,
                Status = StrategyStatus.Killed.ToString(),
                squareOff = new { decision.Action, decision.Quantity, decision.Reason, decision.EstimatedRealizedPnL }
            });
        }
    }

    /// <summary>Value is a plain string, parsed the same way TqqqWeeklyConfigProvider parses
    /// every other config value (e.g. "-0.20" for a decimal, "true"/"false" for a bool).</summary>
    public record SetTqqqWeeklyConfigRequest(string Key, string? Value);
}
