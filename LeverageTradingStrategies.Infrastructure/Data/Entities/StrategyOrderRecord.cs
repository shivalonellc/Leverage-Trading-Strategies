namespace LeverageTradingStrategies.Infrastructure.Data.Entities
{
    public enum StrategyOrderStatus
    {
        Submitted,
        Filled,
        Failed
    }

    public enum StrategyOrderSide
    {
        Buy,
        Sell
    }

    /// <summary>Full audit trail row for one order: what was requested, why (Reason, copied
    /// straight from the strategy decision), and what happened. Written by
    /// StrategyOrderExecutor for BOTH the real broker and the simulated/dry-run broker, using
    /// the exact same code path -- so a dry run produces the same shape of order history a
    /// live run would.</summary>
    public class StrategyOrderRecord
    {
        public long Id { get; set; }
        public int StrategyInstanceId { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty; // mirrors Domain.Tqqq.TqqqWeeklyActionType
        public StrategyOrderSide Side { get; set; }
        public int Quantity { get; set; }
        public string Reason { get; set; } = string.Empty;
        public StrategyOrderStatus Status { get; set; }
        public bool IsSimulated { get; set; }
        public decimal? RequestedPrice { get; set; }
        public decimal? FillPrice { get; set; }
        public decimal? RealizedPnL { get; set; }
        public string? BrokerOrderId { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime SubmittedUtc { get; set; }
        public DateTime? FilledUtc { get; set; }
    }
}
