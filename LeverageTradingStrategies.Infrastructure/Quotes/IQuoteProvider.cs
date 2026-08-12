namespace LeverageTradingStrategies.Infrastructure.Quotes
{
    /// <summary>
    /// Minimal quote shape the weekly strategy actually needs: today's open (for entry/avg-down
    /// fill pricing), the running intraday high (for the take-profit touch check), the current/
    /// last price (for the force-close and close-based-stop checks), and yesterday's close (for
    /// the daily profit/tier calculation). This intentionally does NOT pull in the full 1-min-bar
    /// ingestion/Renko pipeline from MarketMatrixPreparer — a weekly-cadence strategy only needs a
    /// handful of quote fields per tick, polled directly from the broker's own quote endpoint.
    /// </summary>
    public class TqqqQuote
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal LastPrice { get; set; }
        public decimal PreviousClosePrice { get; set; }
        public DateTime AsOfUtc { get; set; }
    }

    public interface IQuoteProvider
    {
        Task<TqqqQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default);
    }
}
