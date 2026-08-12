namespace LeverageTradingStrategies.Infrastructure.Data.Entities
{
    public enum StrategyStatus
    {
        Running,
        Paused,
        Killed
    }

    /// <summary>One row per running strategy (StrategyType + Symbol). Holds the capital
    /// allocation this instance is configured to deploy, whether realized P&L compounds into
    /// future sizing, and the kill/pause switch. Deliberately generic (StrategyType field)
    /// so a future options-seller instance can live in the same table.</summary>
    public class StrategyInstanceRecord
    {
        public int Id { get; set; }
        public string StrategyType { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public decimal AllocatedCapital { get; set; }
        public bool CompoundingEnabled { get; set; }
        public decimal CurrentCapital { get; set; }
        public StrategyStatus Status { get; set; } = StrategyStatus.Running;
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
