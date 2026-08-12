using LeverageTradingStrategies.Infrastructure.Data.Entities;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public interface IStrategyOrderRepository
    {
        /// <summary>Inserts a new order row (Status=Submitted) and returns its assigned Id.</summary>
        Task<long> InsertSubmittedAsync(StrategyOrderRecord order, CancellationToken ct = default);

        Task MarkFilledAsync(long orderId, decimal fillPrice, string? brokerOrderId, decimal? realizedPnL, CancellationToken ct = default);

        Task MarkFailedAsync(long orderId, string errorMessage, CancellationToken ct = default);

        Task<List<StrategyOrderRecord>> GetRecentAsync(int strategyInstanceId, int limit, CancellationToken ct = default);
    }
}
