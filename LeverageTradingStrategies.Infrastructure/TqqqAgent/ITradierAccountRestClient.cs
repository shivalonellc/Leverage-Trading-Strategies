namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public class TradierBalancesDto
    {
        public decimal TotalEquity { get; set; }
        public decimal TotalCash { get; set; }
    }

    public class TradierPositionDto
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal CostBasis { get; set; }
    }

    public class TradierOrderDto
    {
        public long Id { get; set; }
        public string Status { get; set; } = string.Empty; // pending | open | filled | partially_filled | rejected | canceled | expired
        public decimal? AvgFillPrice { get; set; }
        public decimal? ExecQuantity { get; set; }
    }

    /// <summary>Account-scoped Tradier calls (balances/positions/order placement/order status) --
    /// same direct-JSON-over-HttpClient approach as ITradierMarketDataRestClient and for the same
    /// reason (can't verify the NuGet wrapper's exact model property names in this environment,
    /// and this is the highest-stakes part of the whole module -- real orders on a live account).
    /// Shares the same HttpClient instance/registration as the market-data client (same base
    /// address and bearer token); the account id is bound at construction since every call here
    /// is scoped to the one account this module trades.</summary>
    public interface ITradierAccountRestClient
    {
        Task<TradierBalancesDto> GetBalancesAsync(CancellationToken ct = default);

        /// <summary>Empty list if flat (Tradier represents "no positions" as a JSON string
        /// "null" rather than an empty array or object -- handled internally).</summary>
        Task<List<TradierPositionDto>> GetPositionsAsync(CancellationToken ct = default);

        /// <summary>Places a market, day-duration equity order. side must be "buy" or "sell".
        /// Returns the broker order id on success (HTTP 200 with an order id in the response) --
        /// this does NOT mean filled, just accepted; call GetOrderAsync to confirm the fill.</summary>
        Task<(bool Success, long? OrderId, string? ErrorMessage)> PlaceMarketOrderAsync(string symbol, string side, int quantity, CancellationToken ct = default);

        Task<TradierOrderDto?> GetOrderAsync(long orderId, CancellationToken ct = default);
    }
}
