using Microsoft.Extensions.Logging;
using Tradier.Client;

namespace LeverageTradingStrategies.Infrastructure.Options
{
    /// <summary>Thin wrapper over the `tradier-dotnet-client` NuGet package's
    /// TradierClient.MarketData surface -- modeled on RenkoSwingSmartV2's StockDataProvider
    /// (same package, same call shape), trimmed to the options-chain slice this module
    /// actually needs. TradierClient is registered Scoped in Program.cs (same lifetime
    /// pattern as the sibling project), so this provider is Scoped too.</summary>
    public class TradierOptionsProvider : ITradierOptionsProvider
    {
        private readonly TradierClient _tradierClient;
        private readonly ILogger<TradierOptionsProvider> _logger;

        public TradierOptionsProvider(TradierClient tradierClient, ILogger<TradierOptionsProvider> logger)
        {
            _tradierClient = tradierClient;
            _logger = logger;
        }

        public async Task<IReadOnlyList<OptionExpirationDto>> GetExpirationsAsync(string underlyingSymbol, CancellationToken ct = default)
        {
            underlyingSymbol = underlyingSymbol.Trim().ToUpperInvariant();

            var expirations = await _tradierClient.MarketData.GetOptionExpirations(underlyingSymbol);
            if (expirations?.Date == null || expirations.Date.Count == 0)
            {
                _logger.LogWarning("TradierOptionsProvider: no expirations returned for {Symbol}", underlyingSymbol);
                return Array.Empty<OptionExpirationDto>();
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return expirations.Date
                .Select(d => new OptionExpirationDto
                {
                    ExpirationDate = d.Date,
                    DayOfWeek = d.DayOfWeek.ToString(),
                    DaysToExpiration = DateOnly.FromDateTime(d).DayNumber - today.DayNumber
                })
                .Where(e => e.DaysToExpiration >= 0)
                .OrderBy(e => e.ExpirationDate)
                .ToList();
        }

        public async Task<OptionChainDto> GetOptionChainAsync(string underlyingSymbol, DateTime expirationDate, CancellationToken ct = default)
        {
            underlyingSymbol = underlyingSymbol.Trim().ToUpperInvariant();
            var result = new OptionChainDto
            {
                UnderlyingSymbol = underlyingSymbol,
                ExpirationDate = expirationDate.Date,
                RetrievedAtUtc = DateTime.UtcNow
            };

            var chain = await _tradierClient.MarketData.GetOptionChain(underlyingSymbol, expirationDate, true); // true = includeGreeks
            if (chain?.Option == null || chain.Option.Count == 0)
            {
                _logger.LogWarning("TradierOptionsProvider: empty chain for {Symbol} exp {Exp:yyyy-MM-dd}", underlyingSymbol, expirationDate);
                return result;
            }

            foreach (var o in chain.Option)
            {
                if (o?.Greeks == null)
                {
                    // No greeks means the live payoff curve can't price this contract for
                    // "today" (needs IV) -- skip rather than half-populate a contract the
                    // builder/marking job can't actually use.
                    continue;
                }

                try
                {
                    result.Options.Add(new OptionContractDto
                    {
                        Symbol = o.Symbol,
                        UnderlyingSymbol = underlyingSymbol,
                        ExpirationDate = expirationDate.Date,
                        Right = string.Equals(o.OptionType, "call", StringComparison.OrdinalIgnoreCase) ? OptionRight.Call : OptionRight.Put,
                        Strike = Convert.ToDecimal(o.Strike),
                        Bid = Convert.ToDecimal(o.Bid),
                        Ask = Convert.ToDecimal(o.Ask),
                        Last = o.Last > 0 ? Convert.ToDecimal(o.Last) : null,
                        Volume = o.Volume,
                        OpenInterest = o.OpenInterest,
                        Delta = Convert.ToDecimal(o.Greeks.Delta),
                        Gamma = Convert.ToDecimal(o.Greeks.Gamma),
                        Theta = Convert.ToDecimal(o.Greeks.Theta),
                        Vega = Convert.ToDecimal(o.Greeks.Vega),
                        ImpliedVolatility = Convert.ToDecimal(o.Greeks.MidIV)
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TradierOptionsProvider: failed to map contract {Symbol}", o?.Symbol);
                }
            }

            return result;
        }
    }
}
