namespace LeverageTradingStrategies.Infrastructure.Configuration
{
    public interface ITqqqWeeklyConfigProvider
    {
        /// <summary>Ensures this strategy instance has a full set of StrategyConfig rows
        /// (seeding any missing ones from appsettings.json's AppSettings:TqqqWeekly defaults
        /// the first time this is ever called for the instance -- never overwrites a value
        /// that's already been tuned), then returns the current DB-backed values. Cheap
        /// enough to call on every job tick / status request: a handful of parameterized
        /// SQLite reads. Edit a row directly in StrategyConfig and it takes effect on the
        /// very next call -- no app restart required.</summary>
        Task<TqqqWeeklyRuntimeConfig> GetAsync(int strategyInstanceId, CancellationToken ct = default);
    }
}
