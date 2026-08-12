namespace LeverageTradingStrategies.Infrastructure.Options
{
    public sealed class OptionExpirationDto
    {
        public DateTime ExpirationDate { get; set; }
        public string DayOfWeek { get; set; } = string.Empty;
        public int DaysToExpiration { get; set; }
    }

    /// <summary>One contract off Tradier's chain, greeks included (Tradier reports live
    /// greeks/IV per contract -- no local Black-Scholes solve needed for chain data, only for
    /// the live payoff curve's hypothetical-price repricing).</summary>
    public sealed class OptionContractDto
    {
        public string Symbol { get; set; } = string.Empty;          // OCC symbol, e.g. TQQQ260821P00075000
        public string UnderlyingSymbol { get; set; } = string.Empty;
        public DateTime ExpirationDate { get; set; }
        public OptionRight Right { get; set; }
        public decimal Strike { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal? Last { get; set; }
        public long Volume { get; set; }
        public long OpenInterest { get; set; }
        public decimal? Delta { get; set; }
        public decimal? Gamma { get; set; }
        public decimal? Theta { get; set; }
        public decimal? Vega { get; set; }
        public decimal? ImpliedVolatility { get; set; }

        public decimal Mid => Bid > 0 && Ask > 0 ? Math.Round((Bid + Ask) / 2m, 4) : (Last ?? 0m);
        public bool HasValidBidAsk => Bid > 0 && Ask > 0 && Ask >= Bid;
    }

    public sealed class OptionChainDto
    {
        public string UnderlyingSymbol { get; set; } = string.Empty;
        public DateTime ExpirationDate { get; set; }
        public DateTime RetrievedAtUtc { get; set; }
        public List<OptionContractDto> Options { get; set; } = new();

        public IReadOnlyList<OptionContractDto> Calls =>
            Options.Where(o => o.Right == OptionRight.Call).OrderBy(o => o.Strike).ToList();

        public IReadOnlyList<OptionContractDto> Puts =>
            Options.Where(o => o.Right == OptionRight.Put).OrderBy(o => o.Strike).ToList();
    }
}
