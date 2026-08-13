using LeverageTradingStrategies.Domain.TqqqAgent;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public interface ITqqqAgentMarketDataService
    {
        /// <summary>Builds the full TQQQ + QQQ indicator snapshot handed to Claude via
        /// get_market_snapshot (spec §5). marketOpenEt/forceFlattenEt are passed in by the
        /// caller (from TqqqAgentValidatorConfig) rather than hardcoded here, so this service
        /// and the validator can never drift on what "the open" or "the flatten cutover" mean.</summary>
        Task<TqqqAgentMarketSnapshot> GetSnapshotAsync(DateTime nowEastern, TimeSpan marketOpenEt, TimeSpan forceFlattenEt, CancellationToken ct = default);
    }
}
