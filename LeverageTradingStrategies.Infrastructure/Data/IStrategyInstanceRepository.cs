using LeverageTradingStrategies.Infrastructure.Data.Entities;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public interface IStrategyInstanceRepository
    {
        /// <summary>Returns the existing instance for (strategyType, symbol), or creates one
        /// seeded from appsettings config (AllocatedCapital/CompoundingEnabled) if this is the
        /// first time this instance has ever run. Does NOT overwrite AllocatedCapital on an
        /// existing row even if the config value has since changed — use
        /// UpdateAllocatedCapitalAsync to change it deliberately, so a compounded
        /// CurrentCapital never gets silently clobbered by a stale config default.</summary>
        Task<StrategyInstanceRecord> GetOrCreateAsync(string strategyType, string symbol, decimal seedAllocatedCapital, bool seedCompoundingEnabled, CancellationToken ct = default);

        Task<StrategyInstanceRecord?> GetAsync(int id, CancellationToken ct = default);

        Task UpdateStatusAsync(int id, StrategyStatus status, CancellationToken ct = default);

        /// <summary>Sets CurrentCapital directly (e.g. after realized P&L, when compounding is
        /// enabled).</summary>
        Task UpdateCurrentCapitalAsync(int id, decimal newCurrentCapital, CancellationToken ct = default);

        Task UpdateAllocatedCapitalAsync(int id, decimal newAllocatedCapital, CancellationToken ct = default);
    }
}
