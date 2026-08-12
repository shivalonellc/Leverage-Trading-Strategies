namespace LeverageTradingStrategies.Infrastructure.Data
{
    /// <summary>Generic per-instance key/value tuning-parameter store (StrategyConfig table).
    /// Deliberately generic (no strategy-specific columns) so any strategy type can use the
    /// same table -- callers are responsible for their own key naming and value
    /// parsing/formatting (see TqqqWeeklyConfigProvider for the TQQQ weekly convention).</summary>
    public interface IStrategyConfigRepository
    {
        Task<Dictionary<string, string>> GetAllAsync(int strategyInstanceId, CancellationToken ct = default);

        /// <summary>Inserts only the keys that don't already have a row (INSERT OR IGNORE) --
        /// never overwrites a value that's already been tuned, so it's safe to call this on
        /// every resolve without clobbering a live edit.</summary>
        Task SeedDefaultsAsync(int strategyInstanceId, IReadOnlyDictionary<string, string> defaults, CancellationToken ct = default);

        /// <summary>Sets (inserts or overwrites) a single key -- the deliberate-edit path, as
        /// opposed to SeedDefaultsAsync's never-overwrite semantics.</summary>
        Task SetAsync(int strategyInstanceId, string key, string value, CancellationToken ct = default);
    }
}
