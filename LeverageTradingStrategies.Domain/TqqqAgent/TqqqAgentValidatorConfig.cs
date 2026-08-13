namespace LeverageTradingStrategies.Domain.TqqqAgent
{
    /// <summary>Plain DTO mirror of the AppSettings:TqqqAgent config the validator needs --
    /// kept in Domain (no dependency on Infrastructure.Configuration) so
    /// TqqqAgentValidatorService stays a pure, easily-testable class. The job maps
    /// AppSettingsOptions.TqqqAgent onto this once per cycle.</summary>
    public class TqqqAgentValidatorConfig
    {
        public int ConsecutiveLossLimit { get; set; } = 3;
        public decimal DailyLossStopPct { get; set; } = 0.05m;

        // Absolute market-hours gate (check 8, outer clause) -- nothing at all outside this.
        public int MarketOpenHourEt { get; set; } = 9;
        public int MarketOpenMinuteEt { get; set; } = 30;
        public int MarketCloseHourEt { get; set; } = 16;
        public int MarketCloseMinuteEt { get; set; } = 0;

        // New-entry window (check 8, inner clause) -- Buys only allowed inside this sub-range.
        public int EntryWindowStartHourEt { get; set; } = 9;
        public int EntryWindowStartMinuteEt { get; set; } = 35;
        public int EntryWindowEndHourEt { get; set; } = 15;
        public int EntryWindowEndMinuteEt { get; set; } = 40;

        // Forced flatten (check 4) -- past this, any open position is force-sold regardless of
        // what Claude decided.
        public int ForceFlattenHourEt { get; set; } = 15;
        public int ForceFlattenMinuteEt { get; set; } = 45;

        /// <summary>Optional floor on Claude's confidence before a Buy is actually acted on.
        /// Default 0 = disabled, per spec §6 ("give Claude freedom" -- this is a risk-control
        /// knob the user can turn on later, not a constraint on how Claude reasons).</summary>
        public double MinConfidenceToAct { get; set; } = 0.0;
    }
}
