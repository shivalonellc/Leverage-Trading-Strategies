namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public interface ITradierMarketDataRestClient
    {
        /// <summary>Null if Tradier returned no quote for the symbol (e.g. bad symbol, or an
        /// empty response outside all sessions).</summary>
        Task<TradierQuoteDto?> GetQuoteAsync(string symbol, CancellationToken ct = default);

        /// <summary>Bars in [startEt, endEt], Eastern wall-clock times, oldest first. Empty list
        /// (not an exception) if Tradier has nothing for the window -- e.g. called right at the
        /// open before the first bar has closed.</summary>
        Task<List<TradierTimeSalesBarDto>> GetTimeSalesAsync(string symbol, string interval, DateTime startEt, DateTime endEt, CancellationToken ct = default);

        Task<TradierClockDto> GetClockAsync(CancellationToken ct = default);
    }
}
