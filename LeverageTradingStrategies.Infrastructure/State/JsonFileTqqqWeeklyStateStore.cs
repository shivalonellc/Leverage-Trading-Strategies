using LeverageTradingStrategies.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LeverageTradingStrategies.Infrastructure.State
{
    /// <summary>
    /// Simple durable state store: one JSON file per symbol under the configured state
    /// directory. Deliberately not EF/SQLite for v1 — a single-instance weekly-cadence
    /// strategy only persists a handful of scalar fields plus two short rolling lists, so a
    /// flat file avoids a migration/DbContext dependency for something this small. If this
    /// strategy service grows to multiple concurrent instances or needs trade-history
    /// querying, upgrading to the EF+SQLite pattern already used elsewhere
    /// (MarketMatrixPreparer's PyramidCycleState) is the natural next step.
    /// </summary>
    public class JsonFileTqqqWeeklyStateStore : ITqqqWeeklyStateStore
    {
        private readonly ILogger<JsonFileTqqqWeeklyStateStore> _logger;
        private readonly string _directory;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public JsonFileTqqqWeeklyStateStore(ILogger<JsonFileTqqqWeeklyStateStore> logger, string? directory = null)
        {
            _logger = logger;
            _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "state");
            Directory.CreateDirectory(_directory);
        }

        private string PathFor(string symbol) => Path.Combine(_directory, $"tqqq-weekly-{symbol.ToUpperInvariant()}.json");

        public async Task<TqqqWeeklyState> GetOrCreateAsync(string symbol, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var path = PathFor(symbol);
                if (File.Exists(path))
                {
                    var json = await File.ReadAllTextAsync(path, ct);
                    var loaded = JsonSerializer.Deserialize<TqqqWeeklyState>(json, JsonOptions);
                    if (loaded != null)
                        return loaded;
                    _logger.LogWarning("State file {Path} deserialized to null — starting fresh state for {Symbol}", path, symbol);
                }
                return new TqqqWeeklyState { Symbol = symbol.ToUpperInvariant() };
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAsync(TqqqWeeklyState state, CancellationToken ct = default)
        {
            state.LastUpdatedUtc = DateTime.UtcNow;
            await _lock.WaitAsync(ct);
            try
            {
                var path = PathFor(state.Symbol);
                var tmpPath = path + ".tmp";
                var json = JsonSerializer.Serialize(state, JsonOptions);
                await File.WriteAllTextAsync(tmpPath, json, ct);
                File.Move(tmpPath, path, overwrite: true); // atomic-ish swap, avoids a torn write on crash
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
