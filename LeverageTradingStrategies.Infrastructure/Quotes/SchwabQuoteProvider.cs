using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchwabApiCS;

namespace LeverageTradingStrategies.Infrastructure.Quotes
{
    /// <summary>Live quote source backed directly by the Schwab quote endpoint. Same
    /// scoped-SchwabApi-per-call pattern SchwabBroker uses.</summary>
    public class SchwabQuoteProvider : IQuoteProvider
    {
        private readonly ILogger<SchwabQuoteProvider> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public SchwabQuoteProvider(ILogger<SchwabQuoteProvider> logger, IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
        }

        public async Task<TqqqQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return null;

            symbol = symbol.Trim().ToUpperInvariant();

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await schwabApi.GetQuoteAsync(symbol);
                var q = pquote?.Data?.quote;
                if (q == null)
                {
                    _logger.LogWarning("No quote data returned for {Symbol}", symbol);
                    return null;
                }

                return new TqqqQuote
                {
                    Symbol = symbol,
                    OpenPrice = q.openPrice,
                    HighPrice = q.highPrice,
                    LowPrice = q.lowPrice,
                    LastPrice = q.lastPrice != 0 ? q.lastPrice : q.mark,
                    PreviousClosePrice = q.closePrice,
                    AsOfUtc = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch quote for {Symbol}", symbol);
                return null;
            }
        }
    }
}
