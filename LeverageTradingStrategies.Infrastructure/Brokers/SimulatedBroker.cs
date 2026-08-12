using LeverageTradingStrategies.Infrastructure.Models;
using LeverageTradingStrategies.Infrastructure.Quotes;
using System.Collections.Concurrent;

namespace LeverageTradingStrategies.Infrastructure.Brokers
{
    /// <summary>
    /// In-memory stand-in for IBroker + IQuoteProvider, used for dry-run / paper-trading
    /// smoke tests of the live job's control flow (DI wiring, Quartz scheduling, state
    /// persistence, order sequencing) WITHOUT touching a real Schwab account.
    ///
    /// This is NOT a backtest engine — it does not replay historical data or compute
    /// realistic fills. The verified 49.9% CAGR / 22.1% max DD numbers come from the
    /// separate Python replication (see TQQQ_Weekly_Strategy_Spec_v1.md in the
    /// MarketMatrixPreparer repo); this class exists purely so the .NET live-trading
    /// pipeline can be exercised end-to-end before AppSettings:Trading:UseSimulatedBroker
    /// is flipped to false and real orders start going to SchwabBroker.
    ///
    /// Quotes must be seeded via SetQuote before GetQuoteAsync will return anything —
    /// there is no real market data behind this class.
    /// </summary>
    public class SimulatedBroker : IBroker, IQuoteProvider
    {
        private readonly ConcurrentDictionary<string, SymbolPositionInfo> _positions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TqqqQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private decimal _cash;
        private int _nextOrderId = 1;

        public SimulatedBroker(decimal startingCash = 100000m)
        {
            _cash = startingCash;
        }

        /// <summary>Seed or update the simulated quote for a symbol. Call this before the
        /// job ticks in dry-run mode — a real deployment would instead call
        /// SchwabQuoteProvider, which needs no seeding.</summary>
        public void SetQuote(TqqqQuote quote) => _quotes[quote.Symbol.ToUpperInvariant()] = quote;

        public Task<TqqqQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
        {
            _quotes.TryGetValue(symbol.ToUpperInvariant(), out var quote);
            return Task.FromResult(quote);
        }

        public Task<SymbolPositionInfo?> GetSymbolPositionAsync(string accountNumber, string symbol)
        {
            _positions.TryGetValue(symbol.ToUpperInvariant(), out var position);
            return Task.FromResult(position);
        }

        public Task<decimal> GetPortfolioValueAsync(string accountNumber, CancellationToken ct = default)
        {
            decimal positionsValue = _positions.Values.Sum(p => p.MarketValue);
            return Task.FromResult(_cash + positionsValue);
        }

        public Task<string> GetEquityMarketStatus() => Task.FromResult("OPEN");

        private decimal CurrentPrice(string symbol) =>
            _quotes.TryGetValue(symbol.ToUpperInvariant(), out var q) ? q.LastPrice : 0m;

        public Task<string> PlaceBuyMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            symbol = symbol.ToUpperInvariant();
            decimal price = CurrentPrice(symbol);
            _positions.AddOrUpdate(symbol,
                _ => new SymbolPositionInfo { Symbol = symbol, LongQuantity = quantity, AveragePrice = price, MarketValue = price * quantity, Side = PositionSide.Long, AsOfUtc = DateTime.UtcNow },
                (_, existing) =>
                {
                    decimal totalQty = existing.LongQuantity + quantity;
                    decimal blendedAvg = totalQty > 0 ? ((existing.AveragePrice * existing.LongQuantity) + (price * quantity)) / totalQty : price;
                    return new SymbolPositionInfo { Symbol = symbol, LongQuantity = totalQty, AveragePrice = blendedAvg, MarketValue = price * totalQty, Side = PositionSide.Long, AsOfUtc = DateTime.UtcNow };
                });
            _cash -= price * quantity;
            return Task.FromResult(OrderResult(symbol, quantity));
        }

        public Task<string> PlaceSellMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            symbol = symbol.ToUpperInvariant();
            decimal price = CurrentPrice(symbol);
            _positions.TryRemove(symbol, out _);
            _cash += price * quantity;
            return Task.FromResult(OrderResult(symbol, quantity));
        }

        public Task<string> PlaceBuyLimitOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
            => PlaceBuyMarketOrderAsync(accountNumber, symbol, quantity, ct);

        public Task<string> PlaceSellLimitOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
            => PlaceSellMarketOrderAsync(accountNumber, symbol, quantity, ct);

        public Task<string> PlaceStopLossOrderAsync(string accountNumber, string symbol, int quantity, decimal stopPrice, CancellationToken ct = default)
            => Task.FromResult(OrderResult(symbol, quantity)); // accepted, not simulated further — the strategy checks its own close-based stop instead of relying on a resting broker stop order

        public Task<string> PlaceShortSellMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
            => throw new NotSupportedException("SimulatedBroker: TQQQ weekly strategy is long-only, short-selling is not exercised in dry-run mode.");

        public Task<string> PlaceBuyToCoverMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
            => throw new NotSupportedException("SimulatedBroker: TQQQ weekly strategy is long-only, short-covering is not exercised in dry-run mode.");

        public Task<decimal?> GetOrderFillPriceAsync(string accountNumber, string orderId, CancellationToken ct = default)
            => Task.FromResult<decimal?>(CurrentPrice("TQQQ")); // best-effort — dry-run orders fill instantly at the seeded quote

        private string OrderResult(string symbol, int quantity)
        {
            int orderId = Interlocked.Increment(ref _nextOrderId);
            return System.Text.Json.JsonSerializer.Serialize(new { status = "success", orderId = orderId.ToString(), symbol, quantity, simulated = true });
        }
    }
}
