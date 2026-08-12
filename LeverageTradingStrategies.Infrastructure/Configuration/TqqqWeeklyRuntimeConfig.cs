namespace LeverageTradingStrategies.Infrastructure.Configuration
{
    /// <summary>The DB-resolved (StrategyConfig table) values of every TQQQ weekly tuning
    /// parameter, for one strategy instance, as of the moment ITqqqWeeklyConfigProvider.GetAsync
    /// was called. NOT bound from appsettings.json directly -- see TqqqWeeklyConfigProvider for
    /// how this is resolved (appsettings values are only the one-time seed default).</summary>
    public class TqqqWeeklyRuntimeConfig
    {
        // --- Entry sizing ---
        public decimal BaseSizeFraction { get; set; }
        public decimal VolBoostFraction { get; set; }
        public int VolLookbackDays { get; set; }
        public int VolHistoryMaxReadings { get; set; }
        public int VolHistoryMinReadings { get; set; }
        public double VolPercentileThreshold { get; set; }

        // --- Tiered take-profit ---
        public decimal TierHighMultiplier { get; set; }
        public decimal TierMidMultiplier { get; set; }
        public decimal TierLowMultiplier { get; set; }
        public decimal TierProfitHighThreshold { get; set; }

        // --- Standard avg-down ---
        public decimal AvgDownTrigger { get; set; }
        public decimal AvgDownFraction { get; set; }

        // --- Monday-specific avg-down override ---
        public decimal MondayAvgDownTrigger { get; set; }
        public decimal MondayAvgDownFraction { get; set; }

        // --- Close-based stop ---
        public decimal CloseStopPct { get; set; }
        public decimal EntryDayCloseStopPct { get; set; }

        // --- Force-close-weekly ---
        public bool ForceCloseWeekly { get; set; }
        public int ForceCloseHourEt { get; set; }
    }
}
