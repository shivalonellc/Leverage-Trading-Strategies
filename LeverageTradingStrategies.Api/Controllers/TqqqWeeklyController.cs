using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeverageTradingStrategies.Api.Controllers
{
    /// <summary>Read-only status endpoint for the TQQQ weekly strategy — enough to confirm
    /// the job is wired up and see current state at a glance. Manual trigger/backtest
    /// endpoints (matching MarketMatrixPreparer's BacktestController/WeeklyTestController
    /// pattern) are a natural next addition once the live job itself has been smoke-tested.</summary>
    [ApiController]
    [Route("api/tqqq-weekly")]
    public class TqqqWeeklyController : ControllerBase
    {
        private readonly ITqqqWeeklyStateStore _stateStore;
        private readonly IOptions<AppSettingsOptions> _options;

        public TqqqWeeklyController(ITqqqWeeklyStateStore stateStore, IOptions<AppSettingsOptions> options)
        {
            _stateStore = stateStore;
            _options = options;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken ct)
        {
            var tqqq = _options.Value.TqqqWeekly;
            var state = await _stateStore.GetOrCreateAsync(tqqq.Symbol, ct);
            return Ok(new
            {
                config = new
                {
                    tqqq.Enabled,
                    tqqq.Symbol,
                    tqqq.CronSchedule,
                    tqqq.ForceCloseWeekly,
                    useSimulatedBroker = _options.Value.Trading.UseSimulatedBroker
                },
                state
            });
        }
    }
}
