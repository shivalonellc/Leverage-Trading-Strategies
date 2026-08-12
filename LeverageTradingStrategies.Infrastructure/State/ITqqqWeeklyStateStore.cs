using LeverageTradingStrategies.Infrastructure.Models;

namespace LeverageTradingStrategies.Infrastructure.State
{
    public interface ITqqqWeeklyStateStore
    {
        Task<TqqqWeeklyState> GetOrCreateAsync(string symbol, CancellationToken ct = default);
        Task SaveAsync(TqqqWeeklyState state, CancellationToken ct = default);
    }
}
