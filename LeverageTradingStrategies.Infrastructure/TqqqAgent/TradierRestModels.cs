namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Plain DTOs for the slice of Tradier's public REST JSON schema this module reads
    /// directly over HttpClient (see TradierMarketDataRestClient) rather than through the
    /// `tradier-dotnet-client` NuGet wrapper used elsewhere in the codebase (TradierOptionsProvider).
    /// That wrapper's exact C# property names for Quotes/TimeSales/Clock couldn't be confirmed
    /// against primary source in this environment (no local decompiler, GitHub raw source
    /// returned empty for the model files) -- given this module places real trades on a live
    /// account, guessing property names on unverifiable-until-runtime code was an unacceptable
    /// risk. Tradier's actual over-the-wire JSON schema (snake_case fields below) is stable,
    /// public, and has been unchanged for years, so parsing it directly here is the safer bet.
    /// Also NOT used for Trading/Account calls (order placement, balances, positions) -- those
    /// go through the separate ITradierAccountRestClient/TradierAccountRestClient pair instead,
    /// consumed by TqqqAgentBrokerService.</summary>
    public class TradierQuoteDto
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal? Last { get; set; }
        public decimal? Bid { get; set; }
        public decimal? Ask { get; set; }
        public decimal? Open { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Close { get; set; }
        public decimal? PrevClose { get; set; }
        public long? Volume { get; set; }
        public long? AverageVolume { get; set; }
    }

    public class TradierTimeSalesBarDto
    {
        public DateTime Time { get; set; }
        public decimal Price { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long Volume { get; set; }
        public decimal? Vwap { get; set; }
    }

    public class TradierClockDto
    {
        public string State { get; set; } = string.Empty; // "open" | "closed" | "premarket" | "postmarket"
        public string? Description { get; set; }
        public string? NextChange { get; set; }
        public string? NextState { get; set; }
    }
}
