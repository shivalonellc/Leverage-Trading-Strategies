using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.Options
{
    /// <summary>Redis-backed caching decorator in front of <see cref="TradierOptionsProvider"/>.
    /// Registered as the ITradierOptionsProvider implementation in Program.cs, wrapping the
    /// concrete TradierOptionsProvider (which is registered separately under its own type, not
    /// the interface, so DI doesn't wire this decorator to itself).
    ///
    /// Why this exists: every consumer of the chain -- the vertical-spread builder UI (on every
    /// symbol/expiration switch, plus the one "settle" /preview call per drag/slider gesture),
    /// VerticalSpreadMarkingJob (marks every open Paper/Live spread on a cron), and any other
    /// concurrent browser tab -- was hitting Tradier's live API on every single call with zero
    /// caching. A short TTL cache shared across all of them cuts redundant Tradier calls
    /// dramatically without staling the data meaningfully (option bid/ask/greeks don't change
    /// materially within a 10-second window for the DTEs this module trades).
    ///
    /// Resilience: if Redis is unreachable (not running, wrong connection string, network
    /// blip), every cache operation is wrapped so a Redis failure degrades to "just call
    /// Tradier directly" rather than taking the whole endpoint down. This is deliberately
    /// fail-open, not fail-closed -- a live trading/pricing surface should never go dark because
    /// a cache is unavailable.</summary>
    public class CachedTradierOptionsProvider : ITradierOptionsProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default);

        private readonly IDistributedCache _cache;
        private readonly TradierOptionsProvider _inner;
        private readonly ILogger<CachedTradierOptionsProvider> _logger;

        private static readonly TimeSpan ChainTtl = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ExpirationsTtl = TimeSpan.FromSeconds(30);

        public CachedTradierOptionsProvider(IDistributedCache cache, TradierOptionsProvider inner, ILogger<CachedTradierOptionsProvider> logger)
        {
            _cache = cache;
            _inner = inner;
            _logger = logger;
        }

        public async Task<IReadOnlyList<OptionExpirationDto>> GetExpirationsAsync(string underlyingSymbol, CancellationToken ct = default)
        {
            var symbol = underlyingSymbol.Trim().ToUpperInvariant();
            var key = $"tradier:exp:{symbol}";

            var cached = await TryGetAsync<List<OptionExpirationDto>>(key, ct);
            if (cached != null) return cached;

            var fresh = await _inner.GetExpirationsAsync(symbol, ct);
            await TrySetAsync(key, fresh, ExpirationsTtl, ct);
            return fresh;
        }

        public async Task<OptionChainDto> GetOptionChainAsync(string underlyingSymbol, DateTime expirationDate, CancellationToken ct = default)
        {
            var symbol = underlyingSymbol.Trim().ToUpperInvariant();
            var key = $"tradier:chain:{symbol}:{expirationDate:yyyy-MM-dd}";

            var cached = await TryGetAsync<OptionChainDto>(key, ct);
            if (cached != null) return cached;

            var fresh = await _inner.GetOptionChainAsync(symbol, expirationDate, ct);
            await TrySetAsync(key, fresh, ChainTtl, ct);
            return fresh;
        }

        private async Task<T?> TryGetAsync<T>(string key, CancellationToken ct) where T : class
        {
            try
            {
                var bytes = await _cache.GetAsync(key, ct);
                if (bytes == null || bytes.Length == 0) return null;
                return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
            }
            catch (Exception ex)
            {
                // Redis down/unreachable, or a deserialize hiccup after a schema change -- treat
                // exactly like a cache miss. Debug, not Warning: this is expected/frequent chatter
                // if Redis simply isn't running, and shouldn't spam the log at that level.
                _logger.LogDebug(ex, "CachedTradierOptionsProvider: cache read failed for {Key}, falling back to live call", key);
                return null;
            }
        }

        private async Task TrySetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
                await _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
            }
            catch (Exception ex)
            {
                // Failing to populate the cache is never fatal -- the caller already has the
                // freshly-fetched value; this only means the NEXT call won't get a cache hit.
                _logger.LogDebug(ex, "CachedTradierOptionsProvider: cache write failed for {Key}", key);
            }
        }
    }
}
