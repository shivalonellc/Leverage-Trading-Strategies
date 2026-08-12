namespace LeverageTradingStrategies.Infrastructure.Models
{
    /// <summary>
    /// Durable state for one TQQQ weekly strategy instance, persisted across job ticks and
    /// process restarts. Field-by-field, this mirrors the state the Python backtest carries
    /// per-position plus the rolling volatility-gate history — see
    /// TQQQ_Weekly_Strategy_Spec_v1.md for the rule each field supports.
    /// </summary>
    public class TqqqWeeklyState
    {
        public string Symbol { get; set; } = "TQQQ";

        // --- current position ---
        public bool Holding { get; set; }
        public decimal EntryPrice { get; set; }
        public int Quantity { get; set; }
        public DateOnly? EntryDate { get; set; }
        public bool EnteredOnMonday { get; set; }
        public bool AddedThisPosition { get; set; }
        public bool MondayAvgDownWindowConsumed { get; set; }
        public decimal CurrentTargetPrice { get; set; }

        // --- weekly cadence tracking ---
        public int? CurrentIsoWeekKey { get; set; } // year*100 + ISO week number
        public bool TradedThisWeek { get; set; }

        // --- one-time live-deployment guard: defer first-ever entry if launch day is the
        // last trading day of its week (see spec Section 2) ---
        public bool DeployGuardConsumed { get; set; }
        public bool HasEverRun { get; set; }

        // --- volatility gate (Section 6): trailing daily closes to compute the 14-day
        // realized-vol reading, and the rolling history of those readings for percentile
        // ranking. RecentDailyCloses is capped at VolLookbackDays+1; VolHistory at
        // VolHistoryMaxReadings. Both capped/trimmed by RollDailyVolatilityHistory. ---
        public List<decimal> RecentDailyCloses { get; set; } = new();
        public List<double> VolHistory { get; set; } = new();
        public bool VolGateClosedToday { get; set; }

        // --- once-per-trading-day idempotency guards. The live job ticks every few minutes
        // during market hours (see AppSettings:TqqqWeekly:CronSchedule); each of these four
        // phases must run exactly once per trading day no matter how many ticks land inside
        // its window. ---
        public DateOnly? LastSessionOpenDate { get; set; }
        public DateOnly? LastForceCloseCheckDate { get; set; }
        public DateOnly? LastSessionCloseDate { get; set; }
        public DateOnly? LastVolRollDate { get; set; }

        public DateTime? LastUpdatedUtc { get; set; }
    }
}
