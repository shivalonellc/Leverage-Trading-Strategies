namespace LeverageTradingStrategies.Infrastructure.Configuration
{
    /// <summary>Root config binding target for the "AppSettings" section of appsettings.json.</summary>
    public class AppSettingsOptions
    {
        public TradingOptions Trading { get; set; } = new();
        public TqqqWeeklyOptions TqqqWeekly { get; set; } = new();
    }

    public class TradingOptions
    {
        /// <summary>Path to the Schwab OAuth token file consumed by SchwabApiCS.</summary>
        public string SchwabTokenPath { get; set; } = string.Empty;

        public string AccountNumber { get; set; } = string.Empty;

        /// <summary>When true, IBroker resolves to SimulatedBroker instead of SchwabBroker —
        /// use for dry runs / local testing before live capital is at risk.</summary>
        public bool UseSimulatedBroker { get; set; } = true;
    }

    /// <summary>
    /// Every parameter that defines the verified TQQQ weekly strategy baseline
    /// (49.9% CAGR / 22.1% max DD, 2010-02-11 to 2026-08-07 backtest window).
    /// See TQQQ_Weekly_Strategy_Spec_v1.md in the MarketMatrixPreparer repo for the full
    /// rule-by-rule writeup these fields correspond to. Defaults below match that spec exactly
    /// — do not change a default without re-running the Python backtest to confirm the effect.
    /// </summary>
    public class TqqqWeeklyOptions
    {
        public bool Enabled { get; set; } = false;

        public string Symbol { get; set; } = "TQQQ";

        // --- Entry sizing (Section 2 / 6 of the spec) ---
        public decimal BaseSizeFraction { get; set; } = 0.98m;
        public decimal VolBoostFraction { get; set; } = 1.25m;
        public int VolLookbackDays { get; set; } = 14;
        public int VolHistoryMaxReadings { get; set; } = 252;
        public int VolHistoryMinReadings { get; set; } = 60;
        public double VolPercentileThreshold { get; set; } = 0.90;

        // --- Tiered take-profit (Section 4) ---
        public decimal TierHighMultiplier { get; set; } = 1.08177m;
        public decimal TierMidMultiplier { get; set; } = 1.07m;
        public decimal TierLowMultiplier { get; set; } = 1.025m;
        public decimal TierProfitHighThreshold { get; set; } = 0.003m;

        // --- Standard avg-down (Section 5) ---
        public decimal AvgDownTrigger { get; set; } = -0.05m;
        public decimal AvgDownFraction { get; set; } = 0.30m;

        // --- Monday-specific avg-down override (Section 5) ---
        public decimal MondayAvgDownTrigger { get; set; } = -0.03m;
        public decimal MondayAvgDownFraction { get; set; } = 0.20m;

        // --- Close-based stop (Section 7.3) ---
        public decimal CloseStopPct { get; set; } = -0.20m;

        // --- Force-close-weekly (Section 7.2) ---
        public bool ForceCloseWeekly { get; set; } = true;
        public int ForceCloseHourEt { get; set; } = 14; // ~2:00 PM ET, day before last trading day of week

        /// <summary>Quartz cron expression controlling how often the live job ticks during
        /// market hours. Every 5 minutes, Mon-Fri 9am-5pm ET by default — the job itself is
        /// responsible for only acting at the right moments (session open, force-close hour,
        /// session close) and being a no-op on every other tick.</summary>
        public string CronSchedule { get; set; } = "0 */5 9-16 ? * MON-FRI";
    }
}
