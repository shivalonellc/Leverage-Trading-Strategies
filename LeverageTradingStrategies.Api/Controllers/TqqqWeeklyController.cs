using LeverageTradingStrategies.Domain.Orders;
using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
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
        private readonly IQuoteProvider _quoteProvider;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<TqqqWeeklyController> _logger;

        public TqqqWeeklyController(
            ITqqqWeeklyStateStore stateStore,
            ITqqqWeeklyStrategyService strategy,
            IStrategyInstanceRepository instanceRepository,
            IStrategyOrderRepository orderRepository,
            IStrategyOrderExecutor orderExecutor,
            IQuoteProvider quoteProvider,
            IOptions<AppSettingsOptions> options,
            ILogger<TqqqWeeklyController> logger)
        {
            _stateStore = stateStore;
            _strategy = strategy;
            _instanceRepository = instanceRepository;
            _orderRepository = orderRepository;
            _orderExecutor = orderExecutor;
            _quoteProvider = quoteProvider;
            _options = options;
            _logger = logger;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);
            var state = await _stateStore.GetOrCreateAsync(instance.Id, tqqq.Symbol, ct);
            return Ok(new
            {
                config = new
                {
                    tqqq.Enabled,
                    tqqq.Symbol,
                    tqqq.CronSchedule,
                    tqqq.ForceCloseWeekly,
                    tqqq.CloseStopPct,
                    tqqq.EntryDayCloseStopPct,
                    useSimulatedBroker = _options.Value.Trading.UseSimulatedBroker
                },
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

            if (instance.Status == StrategyStatus.Killed)
                return Conflict(new { message = "Cannot pause a Killed instance — it has already been permanently stopped. Nothing else to do." });

            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Paused, ct);
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) PAUSED via API — no new entries until resumed", instance.Id, instance.Symbol);
            return Ok(new { instance.Id, instance.Symbol, Status = StrategyStatus.Paused.ToString() });
        }

        [HttpPost("resume")]
        public async Task<IActionResult> Resume(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            if (instance.Status == StrategyStatus.Killed)
                return Conflict(new { message = "Cannot resume a Killed instance — Killed is permanent for this instance. Restart with a fresh instance if you need to trade this symbol again." });

            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Running, ct);
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) RESUMED via API", instance.Id, instance.Symbol);
            return Ok(new { instance.Id, instance.Symbol, Status = StrategyStatus.Running.ToString() });
        }

        /// <summary>Immediate, synchronous square-off: marks the instance Killed (permanent —
        /// the live job will refuse to act on it ever again) and, if currently holding, sells
        /// the full position right now via the same IStrategyOrderExecutor the live job uses
        /// (so this produces an identically-shaped audit row, real or simulated depending on
        /// AppSettings:Trading:UseSimulatedBroker). Does not wait for the next Quartz tick.</summary>
        [HttpPost("kill")]
        public async Task<IActionResult> Kill(CancellationToken ct)
        {
            var settings = _options.Value;
            var tqqq = settings.TqqqWeekly;
            var instance = await _instanceRepository.GetOrCreateAsync(StrategyType, tqqq.Symbol, tqqq.AllocatedCapital, tqqq.CompoundingEnabled, ct);

            if (instance.Status == StrategyStatus.Killed)
                return Ok(new { instance.Id, instance.Symbol, Status = StrategyStatus.Killed.ToString(), message = "Already Killed — no-op." });

            // Flip the switch FIRST so a concurrently-running job tick (if one happens to be
            // mid-flight) sees Killed and backs off entirely, rather than racing this
            // synchronous flatten below.
            await _instanceRepository.UpdateStatusAsync(instance.Id, StrategyStatus.Killed, ct);
            instance.Status = StrategyStatus.Killed;
            _logger.LogWarning("Strategy instance #{Id} ({Symbol}) KILLED via API — squaring off now", instance.Id, instance.Symbol);

            var quote = await _quoteProvider.GetQuoteAsync(tqqq.Symbol, ct);
            if (quote == null)
            {
                _logger.LogError("Kill switch: no quote available for {Symbol} — status is set to Killed but the position could NOT be squared off automatically. Manual intervention required.", tqqq.Symbol);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    instance.Id,
                    instance.Symbol,
                    Status = StrategyStatus.Killed.ToString(),
                    message = "Status set to Killed, but no quote was available to square off the position. The strategy will place no further orders, but any open position was NOT automatically flattened — close it manually."
                });
            }

            var state = await _stateStore.GetOrCreateAsync(instance.Id, tqqq.Symbol, ct);
            var decision = _strategy.EvaluateKillSwitch(state, quote.LastPrice);
            await _orderExecutor.ExecuteAsync(instance, decision, quote.LastPrice, settings.Trading.AccountNumber, settings.Trading.UseSimulatedBroker, ct);
            await _stateStore.SaveAsync(instance.Id, state, ct);

            return Ok(new
            {
                instance.Id,
                instance.Symbol,
                Status = StrategyStatus.Killed.ToString(),
                squareOff = new { decision.Action, decision.Quantity, decision.Reason, decision.EstimatedRealizedPnL }
            });
        }
    }
}
