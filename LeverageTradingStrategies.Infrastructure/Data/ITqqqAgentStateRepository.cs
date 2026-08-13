namespace LeverageTradingStrategies.Infrastructure.Data
{
    /// <summary>Small ancillary state the TQQQ agent job needs beyond the decision audit trail
    /// itself: the fixed start-of-day equity denominator (TqqqAgentDailyState) and the manual
    /// kill/pause control surface (TqqqAgentControlState). Kept as one repository since both are
    /// tiny single-purpose lookups against single-row/single-key tables -- splitting further
    /// would just add file count without adding clarity.</summary>
    public interface ITqqqAgentStateRepository
    {
        /// <summary>Null if no row exists yet for this Eastern trade date (first cycle of the day
        /// hasn't run). Caller is responsible for then calling SetDayStartEquityIfAbsentAsync.</summary>
        Task<decimal?> GetDayStartEquityAsync(string tradeDateEt, CancellationToken ct = default);

        /// <summary>INSERT OR IGNORE -- safe to call every cycle; only the first call in a given
        /// Eastern day actually writes anything.</summary>
        Task SetDayStartEquityIfAbsentAsync(string tradeDateEt, decimal equity, CancellationToken ct = default);

        Task<TqqqAgentControlStateRow> GetControlStateAsync(CancellationToken ct = default);

        Task SetKilledAsync(bool killed, string? reason, CancellationToken ct = default);

        Task SetPausedAsync(bool paused, string? reason, CancellationToken ct = default);
    }

    public class TqqqAgentControlStateRow
    {
        public bool IsKilled { get; set; }
        public bool IsPaused { get; set; }
        public string? Reason { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
