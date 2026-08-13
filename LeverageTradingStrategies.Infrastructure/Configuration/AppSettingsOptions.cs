namespace LeverageTradingStrategies.Infrastructure.Configuration
{
    /// <summary>Root config binding target for the "AppSettings" section of appsettings.json.</summary>
    public class AppSettingsOptions
    {
        public TradingOptions Trading { get; set; } = new();
        public TqqqWeeklyOptions TqqqWeekly { get; set; } = new();
        public TradierOptions Tradier { get; set; } = new();
        public VerticalSpreadOptions VerticalSpread { get; set; } = new();
        public TqqqAgentOptions TqqqAgent { get; set; } = new();
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

    /// <summary>Tradier is used ONLY for option chain/greeks data (vertical-spread builder +
    /// marking job) — real order execution always goes through Schwab regardless of this
    /// section. Token/AccountId are left blank here deliberately; fill them in locally in
    /// appsettings.json (or user secrets) rather than committing a live API token.</summary>
    public class TradierOptions
    {
        public string Token { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;

        /// <summary>true = Tradier's sandbox/paper endpoint (delayed/simulated data). Since
        /// this module already reads real chain data for a PAPER-tracked position by design,
        /// most setups will want this false (real market data) even while spreads themselves
        /// stay in Paper status — sandbox data is delayed and not representative of real
        /// mark-to-market performance.</summary>
        public bool UseSandbox { get; set; } = false;
    }

    /// <summary>Vertical credit spread module: manually built in the dashboard (symbol,
    /// expiration, strikes), Saved into Paper (mark-to-market tracked against real Tradier
    /// data, no broker order), Deployed into Live (real Schwab combo order) on explicit user
    /// action. See VerticalSpreadMarkingJob for the periodic mark/expiration-close tick.</summary>
    public class VerticalSpreadOptions
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Quartz cron for VerticalSpreadMarkingJob — how often Paper/Live spreads get
        /// a fresh mark-to-market snapshot against the live Tradier chain.</summary>
        public string MarkingCronSchedule { get; set; } = "0 */5 9-16 ? * MON-FRI";
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

    /// <summary>TQQQ intraday discretionary agent (see TQQQ_Intraday_Agent_Spec_v1.md at the
    /// repo root) — Claude decides Hold/Buy/Sell freely each cycle (no hardcoded strategy rules),
    /// TqqqAgentValidatorService enforces the 9 hard risk limits afterward, and 100% of order
    /// placement happens in deterministic C# (TqqqAgentBrokerService), never through anything
    /// Claude itself invokes. Long-only, day-trade-only, TQQQ only, live Tradier account.
    ///
    /// Defaults to Enabled=false and an empty AnthropicApiKey — this job places REAL orders on a
    /// LIVE account the moment both are set. Do not flip Enabled to true until you've filled in
    /// AnthropicApiKey below (get one at https://console.anthropic.com/settings/keys) and are
    /// deliberately ready for it to start trading.</summary>
    public class TqqqAgentOptions
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Fill in your own Anthropic API key here (or via user secrets / an
        /// environment-variable override of this config path) — never commit a real key to this
        /// file. Get one at https://console.anthropic.com/settings/keys.</summary>
        public string AnthropicApiKey { get; set; } = "REPLACE_WITH_YOUR_ANTHROPIC_API_KEY";

        public string AnthropicModel { get; set; } = "claude-sonnet-5";
        public int AnthropicMaxTokens { get; set; } = 1024;
        public int AnthropicMaxToolIterations { get; set; } = 6;

        /// <summary>How often the job ticks during market hours, in minutes — used to build the
        /// Quartz cron trigger at startup (Program.cs). Default 5, per spec.</summary>
        public int IntervalMinutes { get; set; } = 5;

        // --- Position sizing (spec §8): maxNotional = min(availableCash * EquityUsageFraction,
        // MaxNotionalCeiling); shares = floor(maxNotional / currentPrice). ---
        public decimal EquityUsageFraction { get; set; } = 0.85m;
        public decimal MaxNotionalCeiling { get; set; } = 450m;

        // --- Risk limits (spec §7 checks 5/6) ---
        public int ConsecutiveLossLimit { get; set; } = 3;
        public decimal DailyLossStopPct { get; set; } = 0.05m;

        // --- Session timing (ET) -- mirrors TqqqAgentValidatorConfig field-for-field; the job
        // maps these onto that Domain-layer DTO once per cycle. ---
        public int MarketOpenHourEt { get; set; } = 9;
        public int MarketOpenMinuteEt { get; set; } = 30;
        public int MarketCloseHourEt { get; set; } = 16;
        public int MarketCloseMinuteEt { get; set; } = 0;
        public int EntryWindowStartHourEt { get; set; } = 9;
        public int EntryWindowStartMinuteEt { get; set; } = 35;
        public int EntryWindowEndHourEt { get; set; } = 15;
        public int EntryWindowEndMinuteEt { get; set; } = 40;
        public int ForceFlattenHourEt { get; set; } = 15;
        public int ForceFlattenMinuteEt { get; set; } = 45;

        /// <summary>Optional floor on Claude's confidence before a Buy is acted on. 0 = disabled
        /// (default) — per spec §6, Claude has full discretion; this is an off-by-default risk
        /// knob, not a reasoning constraint.</summary>
        public double MinConfidenceToAct { get; set; } = 0.0;

        /// <summary>How many recent decisions Claude sees via get_recent_decisions each cycle.</summary>
        public int RecentDecisionsShownToClaude { get; set; } = 5;

        /// <summary>How many recent decisions the Redis rolling window (ITqqqAgentMemoryService)
        /// keeps in total -- must be >= RecentDecisionsShownToClaude.</summary>
        public int MaxRecentDecisionsKept { get; set; } = 20;
    }
}
