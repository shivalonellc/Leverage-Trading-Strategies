using LeverageTradingStrategies.Infrastructure.TqqqAgent;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public interface ITqqqAgentDecisionRepository
    {
        /// <summary>Inserts a new decision row for this cycle and returns its assigned Id.
        /// OrderStatus starts at "None" -- call UpdateOrderResultAsync afterward once the
        /// validator/order-executor have actually acted (or not) on it.</summary>
        Task<long> InsertAsync(TqqqAgentDecisionRecord record, CancellationToken ct = default);

        Task UpdateOrderResultAsync(
            long id,
            string orderStatus,
            string? brokerOrderId,
            decimal? fillPrice,
            decimal? realizedPnL,
            string? errorMessage,
            CancellationToken ct = default);

        /// <summary>Most recent decisions across all time, newest first -- used to seed/rebuild
        /// the Redis short-term memory and to answer get_recent_decisions if Redis is cold.</summary>
        Task<List<TqqqAgentDecisionRecord>> GetRecentAsync(int limit, CancellationToken ct = default);

        /// <summary>All decisions whose CycleUtc falls in [startUtcInclusive, endUtcExclusive),
        /// oldest first. The job passes today's Eastern-day bounds (converted to UTC via
        /// MarketHoursHelper) to derive RealizedPnLToday and ConsecutiveLossesToday -- filtering
        /// by a UTC range here rather than a TEXT date column keeps this repository timezone-agnostic.</summary>
        Task<List<TqqqAgentDecisionRecord>> GetByCycleRangeAsync(DateTime startUtcInclusive, DateTime endUtcExclusive, CancellationToken ct = default);
    }
}
