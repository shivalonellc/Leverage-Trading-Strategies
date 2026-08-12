using LeverageTradingStrategies.Infrastructure.Models;

namespace LeverageTradingStrategies.Infrastructure.State
{
    /// <summary>Keyed by StrategyInstanceId (the StrategyInstances table row for this
    /// symbol/strategy pair), not by symbol directly — a symbol could in principle run under
    /// more than one strategy type.</summary>
    public interface ITqqqWeeklyStateStore
    {
        Task<TqqqWeeklyState> GetOrCreateAsync(int strategyInstanceId, string symbol, CancellationToken ct = default);
        Task SaveAsync(int strategyInstanceId, TqqqWeeklyState state, CancellationToken ct = default);
    }
}
