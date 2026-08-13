using LeverageTradingStrategies.Domain.TqqqAgent;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Short-term "what have I been doing" memory Claude reads via get_recent_decisions
    /// (spec §5), backed by Redis for fast, cheap reads on a 5-minute cadence, with SQLite
    /// (ITqqqAgentDecisionRepository) as the durable source of truth it rebuilds from if the
    /// cache is cold or Redis is unreachable.</summary>
    public interface ITqqqAgentMemoryService
    {
        Task<List<TqqqAgentRecentDecision>> GetRecentAsync(int limit, CancellationToken ct = default);

        /// <summary>Call once per cycle after the decision (and any order) is final. Best-effort
        /// -- a failed append never throws, since SQLite already has the durable copy.</summary>
        Task AppendAsync(TqqqAgentRecentDecision decision, int maxKept, CancellationToken ct = default);
    }
}
