using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Thin, direct-JSON HttpClient wrapper over Tradier's markets/quotes,
    /// markets/timesales, and markets/clock endpoints -- see TradierRestModels.cs for why this
    /// bypasses the tradier-dotnet-client NuGet wrapper for these three calls. The HttpClient
    /// passed in is expected to already have BaseAddress (sandbox or production, matching
    /// whatever TradierClient elsewhere was configured with) and the Bearer token set as a
    /// default request header -- see Program.cs wiring.
    ///
    /// Tradier's JSON API has a well-known quirk: an array field with exactly one item is
    /// sometimes serialized as a single object instead of a one-element array (and an empty
    /// result as null). Every array read below goes through ReadArrayOrSingle to handle all
    /// three shapes rather than trusting System.Text.Json's array deserializer, which would
    /// throw on the single-object case.</summary>
    public class TradierMarketDataRestClient : ITradierMarketDataRestClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<TradierMarketDataRestClient> _logger;

        public TradierMarketDataRestClient(HttpClient http, ILogger<TradierMarketDataRestClient> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<TradierQuoteDto?> GetQuoteAsync(string symbol, CancellationToken ct = default)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            using var response = await _http.GetAsync($"/v1/markets/quotes?symbols={Uri.EscapeDataString(symbol)}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            if (!doc.RootElement.TryGetProperty("quotes", out var quotesEl) ||
                !quotesEl.TryGetProperty("quote", out var quoteEl) ||
                quoteEl.ValueKind == JsonValueKind.Null)
            {
                _logger.LogWarning("TradierMarketDataRestClient: no quote returned for {Symbol}", symbol);
                return null;
            }

            var first = ReadArrayOrSingle(quoteEl).FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined)
                return null;

            return new TradierQuoteDto
            {
                Symbol = GetString(first, "symbol") ?? symbol,
                Last = GetDecimal(first, "last"),
                Bid = GetDecimal(first, "bid"),
                Ask = GetDecimal(first, "ask"),
                Open = GetDecimal(first, "open"),
                High = GetDecimal(first, "high"),
                Low = GetDecimal(first, "low"),
                Close = GetDecimal(first, "close"),
                PrevClose = GetDecimal(first, "prevclose"),
                Volume = GetLong(first, "volume"),
                AverageVolume = GetLong(first, "average_volume")
            };
        }

        public async Task<List<TradierTimeSalesBarDto>> GetTimeSalesAsync(string symbol, string interval, DateTime startEt, DateTime endEt, CancellationToken ct = default)
        {
            symbol = symbol.Trim().ToUpperInvariant();
            var start = startEt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var end = endEt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var url = $"/v1/markets/timesales?symbol={Uri.EscapeDataString(symbol)}&interval={Uri.EscapeDataString(interval)}" +
                      $"&start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}&session_filter=open";

            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            var results = new List<TradierTimeSalesBarDto>();
            if (!doc.RootElement.TryGetProperty("series", out var seriesEl) ||
                seriesEl.ValueKind == JsonValueKind.Null ||
                !seriesEl.TryGetProperty("data", out var dataEl) ||
                dataEl.ValueKind == JsonValueKind.Null)
            {
                return results; // no bars for this window -- not an error (e.g. right at the open)
            }

            foreach (var bar in ReadArrayOrSingle(dataEl))
            {
                var timeStr = GetString(bar, "time");
                if (timeStr == null || !DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    continue;

                results.Add(new TradierTimeSalesBarDto
                {
                    Time = time,
                    Price = GetDecimal(bar, "price") ?? 0m,
                    Open = GetDecimal(bar, "open") ?? 0m,
                    High = GetDecimal(bar, "high") ?? 0m,
                    Low = GetDecimal(bar, "low") ?? 0m,
                    Close = GetDecimal(bar, "close") ?? 0m,
                    Volume = GetLong(bar, "volume") ?? 0L,
                    Vwap = GetDecimal(bar, "vwap")
                });
            }

            return results;
        }

        public async Task<TradierClockDto> GetClockAsync(CancellationToken ct = default)
        {
            using var response = await _http.GetAsync("/v1/markets/clock", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            if (!doc.RootElement.TryGetProperty("clock", out var clockEl) || clockEl.ValueKind == JsonValueKind.Null)
            {
                _logger.LogWarning("TradierMarketDataRestClient: /markets/clock returned no clock object");
                return new TradierClockDto { State = "unknown" };
            }

            return new TradierClockDto
            {
                State = GetString(clockEl, "state") ?? "unknown",
                Description = GetString(clockEl, "description"),
                NextChange = GetString(clockEl, "next_change"),
                NextState = GetString(clockEl, "next_state")
            };
        }

        /// <summary>Normalizes Tradier's array-or-single-object-or-null quirk into a uniform
        /// sequence of object elements.</summary>
        private static IEnumerable<JsonElement> ReadArrayOrSingle(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    yield return item;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                yield return element;
            }
            // Null/Undefined -> yields nothing.
        }

        private static string? GetString(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind != JsonValueKind.Null
                ? p.ToString()
                : null;

        private static decimal? GetDecimal(JsonElement el, string prop)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p) || p.ValueKind == JsonValueKind.Null)
                return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d))
                return d;
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                return ds;
            return null;
        }

        private static long? GetLong(JsonElement el, string prop)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p) || p.ValueKind == JsonValueKind.Null)
                return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l))
                return l;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var ls))
                return ls;
            return null;
        }
    }
}
