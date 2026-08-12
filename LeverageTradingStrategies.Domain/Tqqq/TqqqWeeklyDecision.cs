namespace LeverageTradingStrategies.Domain.Tqqq
{
    public enum TqqqWeeklyActionType
    {
        None,
        EnterLong,
        AddToPosition,
        SellAll
    }

    /// <summary>Output of one strategy evaluation call — what, if anything, the caller (the
    /// live job) should ask IBroker to do.</summary>
    public class TqqqWeeklyDecision
    {
        public TqqqWeeklyActionType Action { get; set; } = TqqqWeeklyActionType.None;
        public int Quantity { get; set; }
        public string Reason { get; set; } = string.Empty;

        /// <summary>Populated only for SellAll: the true weighted-cost-basis P&L for the
        /// closed position (quantity * requested exit price, minus TotalCostBasis) —
        /// the ESTIMATED figure at decision time, using the price that triggered the exit.
        /// The caller (StrategyOrderExecutor) recomputes the final figure once the actual
        /// fill price is known and that's what actually gets persisted/compounded.</summary>
        public decimal? EstimatedRealizedPnL { get; set; }

        public static TqqqWeeklyDecision None(string reason) => new() { Action = TqqqWeeklyActionType.None, Reason = reason };
        public static TqqqWeeklyDecision EnterLong(int qty, string reason) => new() { Action = TqqqWeeklyActionType.EnterLong, Quantity = qty, Reason = reason };
        public static TqqqWeeklyDecision AddToPosition(int qty, string reason) => new() { Action = TqqqWeeklyActionType.AddToPosition, Quantity = qty, Reason = reason };
        public static TqqqWeeklyDecision SellAll(int qty, string reason, decimal? estimatedRealizedPnL = null) =>
            new() { Action = TqqqWeeklyActionType.SellAll, Quantity = qty, Reason = reason, EstimatedRealizedPnL = estimatedRealizedPnL };
    }
}
