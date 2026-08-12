using LeverageTradingStrategies.Infrastructure.Data.Entities;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public interface IVerticalSpreadRepository
    {
        Task<long> InsertAsync(VerticalSpreadStrategyRecord strategy, CancellationToken ct = default);
        Task<VerticalSpreadStrategyRecord?> GetByIdAsync(long id, CancellationToken ct = default);
        Task<List<VerticalSpreadStrategyRecord>> GetAllAsync(CancellationToken ct = default);

        /// <summary>Status IN (Paper, Live) -- what the marking job iterates every tick.</summary>
        Task<List<VerticalSpreadStrategyRecord>> GetActiveAsync(CancellationToken ct = default);

        Task MarkDeployedAsync(long id, decimal netCreditReceived, CancellationToken ct = default);
        Task MarkFailedAsync(long id, CancellationToken ct = default);
        Task MarkClosedAsync(long id, decimal realizedPnL, string closeReason, CancellationToken ct = default);

        Task<long> InsertOrderAsync(VerticalSpreadOrderRecord order, CancellationToken ct = default);
        Task MarkOrderFilledAsync(long orderId, decimal fillPrice, string? brokerOrderId, CancellationToken ct = default);
        Task MarkOrderRejectedAsync(long orderId, string errorMessage, CancellationToken ct = default);
        Task MarkOrderFailedAsync(long orderId, string errorMessage, CancellationToken ct = default);
        Task<List<VerticalSpreadOrderRecord>> GetOrdersAsync(long strategyId, CancellationToken ct = default);

        Task InsertMarkAsync(VerticalSpreadMarkRecord mark, CancellationToken ct = default);
        Task<List<VerticalSpreadMarkRecord>> GetMarksAsync(long strategyId, int limit, CancellationToken ct = default);
    }
}
