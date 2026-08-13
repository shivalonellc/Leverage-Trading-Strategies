namespace LeverageTradingStrategies.Domain.TqqqAgent
{
    /// <summary>What Claude (or the validator, when it overrides Claude) decided. Long-only,
    /// day-trade-only per TQQQ_Intraday_Agent_Spec_v1.md §3.</summary>
    public enum TqqqAgentAction
    {
        Hold,
        Buy,
        Sell
    }

    /// <summary>The raw decision Claude returned via submit_decision, before validation. Not
    /// necessarily what actually happens -- the validator can downgrade/override this (e.g. force
    /// a Sell at end of day, or reject a Buy that fails a risk check).</summary>
    public class TqqqAgentRawDecision
    {
        public TqqqAgentAction Action { get; set; }
        public double Confidence { get; set; }
        public string Why { get; set; } = string.Empty;
    }

    /// <summary>Current TQQQ position + account state, as handed to Claude via get_portfolio and
    /// consulted by the validator. Read fresh from Tradier + today's SQLite decision rows each
    /// cycle -- never cached across cycles (must reflect the true current state).</summary>
    public class TqqqAgentPortfolioSnapshot
    {
        public bool Holding { get; set; }
        public int Quantity { get; set; }
        public decimal? EntryPrice { get; set; }
        public decimal CashAvailable { get; set; }
        public decimal TotalEquity { get; set; }

        /// <summary>Equity as of the start of today's session -- the denominator for the daily
        /// loss-stop check (check 6), deliberately NOT the same as TotalEquity above (which
        /// already reflects today's realized P&L and would make the % calc circular).</summary>
        public decimal DayStartEquity { get; set; }
        public decimal RealizedPnLToday { get; set; }
        public int ConsecutiveLossesToday { get; set; }
        public bool HaltActive { get; set; }
        public string? HaltReason { get; set; }
    }

    /// <summary>Precomputed indicators for TQQQ (the tradable symbol) and QQQ (regime context,
    /// since TQQQ is a 3x derivative of it) -- see spec §5. All calculation happens in
    /// ITqqqAgentMarketDataService; Claude only ever sees the finished numbers, never raw bars.</summary>
    public class TqqqAgentMarketSnapshot
    {
        public decimal LastPrice { get; set; }
        public decimal Vwap { get; set; }
        public decimal Ema9 { get; set; }
        public decimal Ema20 { get; set; }
        public decimal? Rsi14 { get; set; }
        public decimal? Macd { get; set; }
        public decimal? MacdSignal { get; set; }
        public decimal? MacdHistogram { get; set; }
        public decimal? Atr14 { get; set; }
        public decimal DayHigh { get; set; }
        public decimal DayLow { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal PriorClose { get; set; }
        public decimal GapFromPriorClosePct { get; set; }
        public decimal DistanceFromOpenPct { get; set; }
        public decimal? RelativeVolume { get; set; }

        public decimal QqqLastPrice { get; set; }
        public decimal QqqVwap { get; set; }
        public decimal QqqEma20 { get; set; }
        public bool QqqAboveVwap { get; set; }
        public bool QqqAboveEma20 { get; set; }

        public int MinutesSinceOpen { get; set; }
        public int MinutesUntilForceFlatten { get; set; }
    }

    /// <summary>One row of "recent decisions" context fed back to Claude via
    /// get_recent_decisions, so it has continuity across otherwise-stateless API calls (spec §5).
    /// Sourced from Redis (fast path) with SQLite as the durable fallback/rebuild source.</summary>
    public class TqqqAgentRecentDecision
    {
        public DateTime TimestampUtc { get; set; }
        public TqqqAgentAction Action { get; set; }
        public double Confidence { get; set; }
        public string Why { get; set; } = string.Empty;
        public bool WasExecuted { get; set; }
        public string? RejectReason { get; set; }
        public decimal? RealizedPnL { get; set; }
    }

    /// <summary>Outcome of running the 9 hard-limit checks (spec §7) against a raw decision.
    /// FinalAction is what actually happens -- it can differ from the raw decision's Action when
    /// the validator overrides it (e.g. RequestedAction=Hold but check 4 forces a Sell).</summary>
    public class TqqqAgentValidationResult
    {
        public bool Approved { get; set; }
        public TqqqAgentAction FinalAction { get; set; }
        public int Shares { get; set; }
        public string? RejectReason { get; set; }
        public int FailedCheckNumber { get; set; }
    }

    /// <summary>One full cycle, persisted to TqqqAgentDecisions -- the durable audit trail this
    /// whole module is built around. Snapshot fields are stored as JSON for easy post-hoc review
    /// without needing to reconstruct exact market conditions from other tables.</summary>
    public class TqqqAgentDecisionRecord
    {
        public long Id { get; set; }
        public DateTime CycleUtc { get; set; }
        public string PortfolioSnapshotJson { get; set; } = string.Empty;
        public string MarketSnapshotJson { get; set; } = string.Empty;
        public TqqqAgentAction RawAction { get; set; }
        public double RawConfidence { get; set; }
        public string RawWhy { get; set; } = string.Empty;
        public bool Approved { get; set; }
        public TqqqAgentAction FinalAction { get; set; }
        public int Shares { get; set; }
        public string? RejectReason { get; set; }
        public string OrderStatus { get; set; } = "None"; // None | Submitted | Filled | Failed
        public string? BrokerOrderId { get; set; }
        public decimal? FillPrice { get; set; }
        public decimal? RealizedPnL { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
