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

        public static TqqqWeeklyDecision None(string reason) => new() { Action = TqqqWeeklyActionType.None, Reason = reason };
        public static TqqqWeeklyDecision EnterLong(int qty, string reason) => new() { Action = TqqqWeeklyActionType.EnterLong, Quantity = qty, Reason = reason };
        public static TqqqWeeklyDecision AddToPosition(int qty, string reason) => new() { Action = TqqqWeeklyActionType.AddToPosition, Quantity = qty, Reason = reason };
        public static TqqqWeeklyDecision SellAll(int qty, string reason) => new() { Action = TqqqWeeklyActionType.SellAll, Quantity = qty, Reason = reason };
    }
}
