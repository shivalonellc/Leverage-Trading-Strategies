using System.Text.Json;
using LeverageTradingStrategies.Domain.TqqqAgent;
using LeverageTradingStrategies.Infrastructure.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Whole-list-as-one-blob Redis cache, same shape as CachedTradierOptionsProvider
    /// (IDistributedCache get/set of a JSON blob, wrapped in try/catch so a Redis outage
    /// degrades gracefully) -- just storing a rolling window of recent decisions instead of an
    /// options chain. Uses the "tqqqagent:" key prefix on the same lts: Redis instance/database
    /// everything else in this app shares (see Program.cs's AddStackExchangeRedisCache).
    ///
    /// No TTL on the cached blob: unlike option chain data, which goes stale within seconds,
    /// this is a small rolling window maintained by AppendAsync's own trim -- it should survive
    /// for the whole trading day (and beyond; a stale entry from yesterday is harmless context,
    /// still true information about a past decision).</summary>
    public class TqqqAgentMemoryService : ITqqqAgentMemoryService
    {
        private const string CacheKey = "tqqqagent:recent-decisions";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default);

        private readonly IDistributedCache _cache;
        private readonly ITqqqAgentDecisionRepository _decisionRepository;
        private readonly ILogger<TqqqAgentMemoryService> _logger;

        public TqqqAgentMemoryService(IDistributedCache cache, ITqqqAgentDecisionRepository decisionRepository, ILogger<TqqqAgentMemoryService> logger)
        {
            _cache = cache;
            _decisionRepository = decisionRepository;
            _logger = logger;
        }

        public async Task<List<TqqqAgentRecentDecision>> GetRecentAsync(int limit, CancellationToken ct = default)
        {
            var cached = await TryGetAsync(ct);
            if (cached is { Count: > 0 })
                return cached.Take(limit).ToList();

            // Cold cache or Redis unreachable -- rebuild from the durable SQLite audit trail and
            // best-effort re-seed Redis so the next cycle gets a warm read.
            var fromDb = await _decisionRepository.GetRecentAsync(Math.Max(limit, 10), ct);
            var rebuilt = fromDb.Select(ToRecentDecision).ToList();
            await TrySetAsync(rebuilt, ct);
            return rebuilt.Take(limit).ToList();
        }

        public async Task AppendAsync(TqqqAgentRecentDecision decision, int maxKept, CancellationToken ct = default)
        {
            var current = await TryGetAsync(ct) ?? new List<TqqqAgentRecentDecision>();
            current.Insert(0, decision);
            if (current.Count > maxKept)
                current = current.Take(maxKept).ToList();
            await TrySetAsync(current, ct);
        }

        private static TqqqAgentRecentDecision ToRecentDecision(TqqqAgentDecisionRecord r) => new()
        {
            TimestampUtc = r.CycleUtc,
            Action = r.FinalAction,
            Confidence = r.RawConfidence,
            Why = r.RawWhy,
            WasExecuted = r.OrderStatus == "Filled",
            RejectReason = r.RejectReason,
            RealizedPnL = r.RealizedPnL
        };

        private async Task<List<TqqqAgentRecentDecision>?> TryGetAsync(CancellationToken ct)
        {
            try
            {
                var bytes = await _cache.GetAsync(CacheKey, ct);
                if (bytes == null || bytes.Length == 0) return null;
                return JsonSerializer.Deserialize<List<TqqqAgentRecentDecision>>(bytes, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TqqqAgentMemoryService: cache read failed, treating as empty");
                return null;
            }
        }

        private async Task TrySetAsync(List<TqqqAgentRecentDecision> value, CancellationToken ct)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
                await _cache.SetAsync(CacheKey, bytes, new DistributedCacheEntryOptions(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TqqqAgentMemoryService: cache write failed");
            }
        }
    }
}
