using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.TqqqAgent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeverageTradingStrategies.Api.Controllers
{
    /// <summary>Status, decision history, and kill/pause/resume controls for the TQQQ intraday
    /// discretionary agent. Backs the monitoring dashboard. There is only one instance of this
    /// module (single account, single symbol) -- no instanceId route parameter, unlike the
    /// generic strategy framework's controllers.</summary>
    [ApiController]
    [Route("api/tqqq-agent")]
    public class TqqqAgentController : ControllerBase
    {
        private readonly ITqqqAgentStateRepository _stateRepository;
        private readonly ITqqqAgentDecisionRepository _decisionRepository;
        private readonly ITqqqAgentBrokerService _broker;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<TqqqAgentController> _logger;

        public TqqqAgentController(
            ITqqqAgentStateRepository stateRepository,
            ITqqqAgentDecisionRepository decisionRepository,
            ITqqqAgentBrokerService broker,
            IOptions<AppSettingsOptions> options,
            ILogger<TqqqAgentController> logger)
        {
            _stateRepository = stateRepository;
            _decisionRepository = decisionRepository;
            _broker = broker;
            _options = options;
            _logger = logger;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken ct)
        {
            var cfg = _options.Value.TqqqAgent;
            var controlState = await _stateRepository.GetControlStateAsync(ct);
            var recentDecisions = await _decisionRepository.GetRecentAsync(1, ct);

            return Ok(new
            {
                config = new
                {
                    cfg.Enabled,
                    cfg.IntervalMinutes,
                    cfg.EquityUsageFraction,
                    cfg.MaxNotionalCeiling,
                    cfg.ConsecutiveLossLimit,
                    cfg.DailyLossStopPct,
                    cfg.MarketOpenHourEt,
                    cfg.MarketOpenMinuteEt,
                    cfg.EntryWindowStartHourEt,
                    cfg.EntryWindowStartMinuteEt,
                    cfg.EntryWindowEndHourEt,
                    cfg.EntryWindowEndMinuteEt,
                    cfg.ForceFlattenHourEt,
                    cfg.ForceFlattenMinuteEt,
                    cfg.MinConfidenceToAct,
                    cfg.AnthropicModel,
                    anthropicApiKeyConfigured = !string.IsNullOrWhiteSpace(cfg.AnthropicApiKey) && cfg.AnthropicApiKey != "REPLACE_WITH_YOUR_ANTHROPIC_API_KEY"
                    // AnthropicApiKey itself is never returned to a client.
                },
                controlState = new { controlState.IsKilled, controlState.IsPaused, controlState.Reason, controlState.UpdatedUtc },
                lastDecision = recentDecisions.FirstOrDefault()
            });
        }

        [HttpGet("decisions")]
        public async Task<IActionResult> GetDecisions([FromQuery] int limit, CancellationToken ct)
        {
            var effectiveLimit = limit <= 0 ? 50 : Math.Min(limit, 500);
            var decisions = await _decisionRepository.GetRecentAsync(effectiveLimit, ct);
            return Ok(decisions);
        }

        /// <summary>Broker-confirmed snapshot of what a Kill would do right now -- read-only,
        /// does not touch control state or place any order.</summary>
        [HttpGet("kill-preview")]
        public async Task<IActionResult> KillPreview(CancellationToken ct)
        {
            var controlState = await _stateRepository.GetControlStateAsync(ct);
            TqqqAgentPortfolioPreview? portfolio;
            try
            {
                var snapshot = await _broker.GetPortfolioSnapshotAsync(0m, 0m, 0, false, null, ct);
                portfolio = new TqqqAgentPortfolioPreview(snapshot.Holding, snapshot.Quantity, snapshot.EntryPrice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TqqqAgentController: kill-preview could not fetch broker position");
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not reach Tradier to confirm the current position. Try again before killing." });
            }

            return Ok(new
            {
                alreadyKilled = controlState.IsKilled,
                brokerQuantity = portfolio.Quantity,
                brokerEntryPrice = portfolio.EntryPrice,
                willSquareOff = portfolio.Holding,
                message = portfolio.Holding
                    ? $"Broker shows {portfolio.Quantity} share(s) of TQQQ open — killing will sell all {portfolio.Quantity} at market and stop new decisions until Resumed."
                    : "Broker shows no open TQQQ position — killing will just stop new decisions (nothing to sell)."
            });
        }

        /// <summary>Immediate, synchronous square-off, same safety ordering as
        /// TqqqWeeklyController.Kill: paused first (blocks a concurrently-running job tick and
        /// is the safe fallback state), position confirmed against the live broker, order placed
        /// if needed, and ONLY marked Killed once the square-off is confirmed (or confirmed
        /// already flat). Anything failing along the way leaves the agent Paused, never
        /// silently Killed with a stranded position.</summary>
        [HttpPost("kill")]
        public async Task<IActionResult> Kill(CancellationToken ct)
        {
            var controlState = await _stateRepository.GetControlStateAsync(ct);
            if (controlState.IsKilled)
                return Ok(new { Status = "Killed", message = "Already Killed — no-op." });

            await _stateRepository.SetPausedAsync(true, "Kill in progress", ct);

            TqqqAgentPortfolioPreview portfolio;
            try
            {
                var snapshot = await _broker.GetPortfolioSnapshotAsync(0m, 0m, 0, false, null, ct);
                portfolio = new TqqqAgentPortfolioPreview(snapshot.Holding, snapshot.Quantity, snapshot.EntryPrice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TqqqAgentController: Kill could not confirm current position — kill NOT completed, left Paused");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    Status = "Paused",
                    message = "Could not confirm the current position with Tradier — kill was NOT completed. The agent is Paused (no new entries) but not Killed. Fix connectivity and retry Kill, or Resume to keep trading."
                });
            }

            if (!portfolio.Holding)
            {
                await _stateRepository.SetKilledAsync(true, "Killed via API — no open position", ct);
                _logger.LogWarning("TqqqAgentController: KILLED via API — no open position at broker");
                return Ok(new { Status = "Killed", message = "Broker confirmed no open position — nothing to square off. Agent Killed." });
            }

            var orderResult = await _broker.PlaceOrderAsync("sell", portfolio.Quantity, ct);
            if (!orderResult.Success)
            {
                _logger.LogError("TqqqAgentController: Kill square-off order FAILED: {Error} — left Paused so it can be retried", orderResult.ErrorMessage);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    Status = "Paused",
                    message = $"The square-off order failed at Tradier ({orderResult.ErrorMessage}) — kill was NOT completed. Retry Kill, or Resume to keep trading with the position still open."
                });
            }

            decimal? realizedPnL = portfolio.EntryPrice.HasValue && orderResult.FillPrice.HasValue
                ? (orderResult.FillPrice.Value - portfolio.EntryPrice.Value) * orderResult.FilledQuantity
                : null;

            await _decisionRepository.InsertAsync(new TqqqAgentDecisionRecord
            {
                CycleUtc = DateTime.UtcNow,
                PortfolioSnapshotJson = System.Text.Json.JsonSerializer.Serialize(portfolio),
                MarketSnapshotJson = "{}",
                RawAction = TqqqAgentAction.Sell,
                RawConfidence = 1.0,
                RawWhy = "Manual kill switch via API",
                Approved = true,
                FinalAction = TqqqAgentAction.Sell,
                Shares = portfolio.Quantity,
                OrderStatus = orderResult.Status,
                BrokerOrderId = orderResult.BrokerOrderId,
                FillPrice = orderResult.FillPrice,
                RealizedPnL = realizedPnL
            }, ct);

            await _stateRepository.SetKilledAsync(true, "Killed via API — position squared off", ct);
            _logger.LogWarning("TqqqAgentController: KILLED via API — position squared off ({Shares} shares, order {OrderId})", portfolio.Quantity, orderResult.BrokerOrderId);

            return Ok(new
            {
                Status = "Killed",
                squareOff = new { action = "Sell", shares = portfolio.Quantity, orderResult.Status, orderResult.BrokerOrderId, orderResult.FillPrice, realizedPnL }
            });
        }

        /// <summary>Pause = no new entries; an existing position keeps being fully managed
        /// (including the job's own forced end-of-day flatten) until it closes out.</summary>
        [HttpPost("pause")]
        public async Task<IActionResult> Pause(CancellationToken ct)
        {
            await _stateRepository.SetPausedAsync(true, "Paused via API", ct);
            _logger.LogWarning("TqqqAgentController: PAUSED via API — no new entries until resumed");
            return Ok(new { Status = "Paused" });
        }

        /// <summary>Resume clears BOTH Paused and Killed -- like TqqqWeekly, Kill is a strong
        /// stop, not a one-way door. Does not re-open any position on its own.</summary>
        [HttpPost("resume")]
        public async Task<IActionResult> Resume(CancellationToken ct)
        {
            await _stateRepository.SetPausedAsync(false, null, ct);
            await _stateRepository.SetKilledAsync(false, null, ct);
            _logger.LogWarning("TqqqAgentController: RESUMED via API");
            return Ok(new { Status = "Running" });
        }

        private record TqqqAgentPortfolioPreview(bool Holding, int Quantity, decimal? EntryPrice);
    }
}
