using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public class TradierAccountRestClient : ITradierAccountRestClient
    {
        private readonly HttpClient _http;
        private readonly string _accountId;
        private readonly ILogger<TradierAccountRestClient> _logger;

        public TradierAccountRestClient(HttpClient http, string accountId, ILogger<TradierAccountRestClient> logger)
        {
            _http = http;
            _accountId = accountId;
            _logger = logger;
        }

        public async Task<TradierBalancesDto> GetBalancesAsync(CancellationToken ct = default)
        {
            using var response = await _http.GetAsync($"/v1/accounts/{_accountId}/balances", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            if (!doc.RootElement.TryGetProperty("balances", out var balancesEl) || balancesEl.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("TradierAccountRestClient: /balances returned no balances object");
                return new TradierBalancesDto();
            }

            // total_equity and total_cash are present across account types (cash/margin/PDT) --
            // deliberately not reading the type-specific "cash"/"margin" sub-objects since the
            // sizing formula (spec §8) only needs these two top-level figures.
            return new TradierBalancesDto
            {
                TotalEquity = GetDecimal(balancesEl, "total_equity") ?? 0m,
                TotalCash = GetDecimal(balancesEl, "total_cash") ?? 0m
            };
        }

        public async Task<List<TradierPositionDto>> GetPositionsAsync(CancellationToken ct = default)
        {
            using var response = await _http.GetAsync($"/v1/accounts/{_accountId}/positions", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            var results = new List<TradierPositionDto>();
            if (!doc.RootElement.TryGetProperty("positions", out var positionsEl))
                return results;

            // Tradier's well-known quirk: with no open positions, "positions" is the JSON
            // *string* "null" (not a JSON null, not an empty object) -- guard for both that and
            // an actual null before looking for the nested "position" field.
            if (positionsEl.ValueKind == JsonValueKind.String || positionsEl.ValueKind == JsonValueKind.Null)
                return results;

            if (positionsEl.ValueKind != JsonValueKind.Object || !positionsEl.TryGetProperty("position", out var positionEl))
                return results;

            foreach (var p in ReadArrayOrSingle(positionEl))
            {
                var symbol = GetString(p, "symbol");
                if (symbol == null)
                    continue;

                results.Add(new TradierPositionDto
                {
                    Symbol = symbol,
                    Quantity = GetDecimal(p, "quantity") ?? 0m,
                    CostBasis = GetDecimal(p, "cost_basis") ?? 0m
                });
            }
            return results;
        }

        public async Task<(bool Success, long? OrderId, string? ErrorMessage)> PlaceMarketOrderAsync(string symbol, string side, int quantity, CancellationToken ct = default)
        {
            side = side.Trim().ToLowerInvariant();
            if (side != "buy" && side != "sell")
                return (false, null, $"Invalid side '{side}' -- must be 'buy' or 'sell'.");

            var form = new Dictionary<string, string>
            {
                ["class"] = "equity",
                ["symbol"] = symbol.Trim().ToUpperInvariant(),
                ["side"] = side,
                ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
                ["type"] = "market",
                ["duration"] = "day"
            };

            using var response = await _http.PostAsync($"/v1/accounts/{_accountId}/orders", new FormUrlEncodedContent(form), ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("TradierAccountRestClient: order placement failed {Status}: {Body}", response.StatusCode, body);
                return (false, null, $"Tradier returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("order", out var orderEl) || orderEl.ValueKind != JsonValueKind.Object)
                return (false, null, $"Unexpected order-placement response shape: {body}");

            var status = GetString(orderEl, "status");
            var id = GetLong(orderEl, "id");
            if (id == null || (status != null && status.Equals("rejected", StringComparison.OrdinalIgnoreCase)))
                return (false, id, $"Order not accepted (status={status ?? "unknown"}).");

            return (true, id, null);
        }

        public async Task<TradierOrderDto?> GetOrderAsync(long orderId, CancellationToken ct = default)
        {
            using var response = await _http.GetAsync($"/v1/accounts/{_accountId}/orders/{orderId}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            if (!doc.RootElement.TryGetProperty("order", out var orderEl) || orderEl.ValueKind != JsonValueKind.Object)
                return null;

            return new TradierOrderDto
            {
                Id = GetLong(orderEl, "id") ?? orderId,
                Status = GetString(orderEl, "status") ?? "unknown",
                AvgFillPrice = GetDecimal(orderEl, "avg_fill_price"),
                ExecQuantity = GetDecimal(orderEl, "exec_quantity")
            };
        }

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
