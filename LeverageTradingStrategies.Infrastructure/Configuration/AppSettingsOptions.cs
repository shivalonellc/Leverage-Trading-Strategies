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

        // --- Capital allocation / compounding ---
        // Only used to SEED the StrategyInstances row the first time this instance ever
        // runs. After that, the DB row (AllocatedCapital/CompoundingEnabled/CurrentCapital)
        // is the source of truth — change it via the controller endpoints, not by editing
        // these and restarting (see IStrategyInstanceRepository remarks).
        /// <summary>Dollar amount this strategy instance is allowed to deploy. All sizing
        /// math (entry qty, avg-down qty) is a fraction of this, NOT of total account equity
        /// — lets more than one strategy share a single brokerage account safely.</summary>
        public decimal AllocatedCapital { get; set; } = 10000m;

        /// <summary>If true, realized P&L from each closed trade rolls into CurrentCapital,
        /// so next week's sizing reflects compounded gains/losses. If false, CurrentCapital
        /// always resets back to AllocatedCapital (fixed-size trading regardless of P&L).</summary>
        public bool CompoundingEnabled { get; set; } = false;

        // --- Every field below this point (through ForceCloseHourEt) is a SEED DEFAULT only.
        // TqqqWeeklyConfigProvider copies these into the StrategyConfig DB table the first
        // time each strategy instance ever resolves its config, and the DB row is the source
        // of truth after that -- editing these values and restarting has NO effect on an
        // already-running instance. Edit StrategyConfig directly (or a future admin endpoint)
        // to retune a live instance; it takes effect on the very next job tick. ---

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

        /// <summary>NEW vs. the backtested baseline: a close-based stop-loss that ALSO
        /// applies on the entry day itself (the verified backtest deliberately has no stop
        /// check at all on the entry day — the "entry-day blind spot" — this adds one back
        /// for live risk management, at the user's request). Checked only at that day's
        /// close, same mechanism as the existing non-entry-day stop, just a separately
        /// configurable threshold so the two can be tuned independently later.</summary>
        public decimal EntryDayCloseStopPct { get; set; } = -0.20m;

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
