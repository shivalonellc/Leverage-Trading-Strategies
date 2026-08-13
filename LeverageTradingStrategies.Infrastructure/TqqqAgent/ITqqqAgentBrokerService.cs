using LeverageTradingStrategies.Domain.TqqqAgent;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public class TqqqAgentOrderResult
    {
        public bool Success { get; set; }
        public string? BrokerOrderId { get; set; }
        public decimal? FillPrice { get; set; }
        public int FilledQuantity { get; set; }
        public string Status { get; set; } = "Failed"; // Filled | Submitted | Failed
        public string? ErrorMessage { get; set; }
    }

    /// <summary>Everything the job needs from the live Tradier account: today's live TQQQ
    /// position/cash (combined with day-level stats the job already tracked in SQLite, to build
    /// the full TqqqAgentPortfolioSnapshot) and market-order placement with fill confirmation.</summary>
    public interface ITqqqAgentBrokerService
    {
        Task<TqqqAgentPortfolioSnapshot> GetPortfolioSnapshotAsync(
            decimal dayStartEquity,
            decimal realizedPnLToday,
            int consecutiveLossesToday,
            bool haltActive,
            string? haltReason,
            CancellationToken ct = default);

        /// <summary>Places a market day order for TQQQ and polls briefly for a fill. side must be
        /// "buy" or "sell". Never throws -- failures come back as Success=false with
        /// ErrorMessage set, matching how the job needs to record a Failed order row either way.</summary>
        Task<TqqqAgentOrderResult> PlaceOrderAsync(string side, int quantity, CancellationToken ct = default);
    }
}
