using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Quotes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LeverageTradingStrategies.Api.Controllers
{
    /// <summary>
    /// Diagnostic endpoints for exercising the broker plumbing end-to-end (account lookup,
    /// market entry, stop-loss, take-profit, close) OUTSIDE the strategy jobs -- for
    /// confirming the Schwab wiring actually works before trusting it inside a live strategy.
    ///
    /// Every order-placing endpoint here takes a "live" flag (default false):
    ///   - live=false (default): routes to SimulatedBroker -- safe, no real order, no real
    ///     account touched, regardless of what AppSettings:Trading:UseSimulatedBroker is set
    ///     to for the live trading job.
    ///   - live=true: routes to the REAL SchwabBroker against AppSettings:Trading:AccountNumber.
    ///     THIS PLACES A REAL ORDER WITH REAL MONEY. Every call is logged at Warning level.
    ///
    /// Quantity is hard-coded to 1 on every order endpoint -- deliberately not a parameter --
    /// so a live=true test can never accidentally place a large order.
    /// </summary>
    [ApiController]
    [Route("api/broker-test")]
    public class BrokerTestController : ControllerBase
    {
        private const int Quantity = 1;

        private readonly SchwabBroker _schwabBroker;
        private readonly SimulatedBroker _simulatedBroker;
        private readonly SchwabQuoteProvider _schwabQuoteProvider;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<BrokerTestController> _logger;

        public BrokerTestController(
            SchwabBroker schwabBroker,
            SimulatedBroker simulatedBroker,
            SchwabQuoteProvider schwabQuoteProvider,
            IOptions<AppSettingsOptions> options,
            ILogger<BrokerTestController> logger)
        {
            _schwabBroker = schwabBroker;
            _simulatedBroker = simulatedBroker;
            _schwabQuoteProvider = schwabQuoteProvider;
            _options = options;
            _logger = logger;
        }

        /// <summary>Account equity, current position for the symbol, and market status.</summary>
        [HttpGet("account")]
        public async Task<IActionResult> GetAccountInfo([FromQuery] bool live = false, [FromQuery] string? symbol = null, CancellationToken ct = default)
        {
            var invalid = ValidateLiveReady(live);
            if (invalid != null) return invalid;

            var accountNumber = _options.Value.Trading.AccountNumber;
            var sym = ResolveSymbol(symbol);
            var broker = ResolveBroker(live);

            _logger.LogInformation("BrokerTest: account info requested (live={Live}, symbol={Symbol})", live, sym);

            try
            {
                var portfolioValue = await broker.GetPortfolioValueAsync(accountNumber, ct);
                var position = await broker.GetSymbolPositionAsync(accountNumber, sym);
                var marketStatus = await broker.GetEquityMarketStatus();

                return Ok(new { live, accountNumber, symbol = sym, marketStatus, portfolioValue, position });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BrokerTest: account info lookup failed (live={Live})", live);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message, live });
            }
        }

        /// <summary>Places a BUY market order for 1 share.</summary>
        [HttpPost("order/market")]
        public async Task<IActionResult> PlaceMarketOrder([FromQuery] string? symbol, [FromQuery] bool live = false, CancellationToken ct = default)
        {
            var invalid = ValidateLiveReady(live);
            if (invalid != null) return invalid;

            var sym = ResolveSymbol(symbol);
            var accountNumber = _options.Value.Trading.AccountNumber;
            var broker = ResolveBroker(live);

            _logger.LogWarning("BrokerTest: PLACE MARKET ORDER {Symbol} x{Qty} (live={Live})", sym, Quantity, live);
            var result = await broker.PlaceBuyMarketOrderAsync(accountNumber, sym, Quantity, ct);
            return Ok(new { live, quantity = Quantity, symbol = sym, brokerResponse = ParseBrokerResponse(result) });
        }

        /// <summary>Places a SELL stop order for 1 share. If stopPrice is omitted, it's
        /// computed as stopPercent below the current quote (default 5%).</summary>
        [HttpPost("order/stop-loss")]
        public async Task<IActionResult> PlaceStopLoss(
            [FromQuery] string? symbol,
            [FromQuery] decimal? stopPrice,
            [FromQuery] decimal stopPercent = 5m,
            [FromQuery] bool live = false,
            CancellationToken ct = default)
        {
            var invalid = ValidateLiveReady(live);
            if (invalid != null) return invalid;

            var sym = ResolveSymbol(symbol);
            var accountNumber = _options.Value.Trading.AccountNumber;
            var broker = ResolveBroker(live);
            var quoteProvider = ResolveQuoteProvider(live);

            decimal effectiveStopPrice;
            if (stopPrice.HasValue)
            {
                effectiveStopPrice = stopPrice.Value;
            }
            else
            {
                var quote = await quoteProvider.GetQuoteAsync(sym, ct);
                if (quote == null || quote.LastPrice <= 0)
                    return BadRequest(new { message = $"No usable quote available for {sym} to compute a default stop price — pass stopPrice explicitly." });
                effectiveStopPrice = Math.Round(quote.LastPrice * (1 - (stopPercent / 100m)), 2);
            }

            _logger.LogWarning("BrokerTest: PLACE STOP-LOSS {Symbol} x{Qty} @ {StopPrice} (live={Live})", sym, Quantity, effectiveStopPrice, live);
            var result = await broker.PlaceStopLossOrderAsync(accountNumber, sym, Quantity, effectiveStopPrice, ct);
            return Ok(new { live, quantity = Quantity, symbol = sym, stopPrice = effectiveStopPrice, brokerResponse = ParseBrokerResponse(result) });
        }

        /// <summary>Places a SELL limit order for 1 share. If limitPrice is omitted, it's
        /// computed as profitPercent above the current quote (default 5%).</summary>
        [HttpPost("order/take-profit")]
        public async Task<IActionResult> PlaceTakeProfit(
            [FromQuery] string? symbol,
            [FromQuery] decimal? limitPrice,
            [FromQuery] decimal profitPercent = 5m,
            [FromQuery] bool live = false,
            CancellationToken ct = default)
        {
            var invalid = ValidateLiveReady(live);
            if (invalid != null) return invalid;

            var sym = ResolveSymbol(symbol);
            var accountNumber = _options.Value.Trading.AccountNumber;
            var broker = ResolveBroker(live);
            var quoteProvider = ResolveQuoteProvider(live);

            decimal effectiveLimitPrice;
            if (limitPrice.HasValue)
            {
                effectiveLimitPrice = limitPrice.Value;
            }
            else
            {
                var quote = await quoteProvider.GetQuoteAsync(sym, ct);
                if (quote == null || quote.LastPrice <= 0)
                    return BadRequest(new { message = $"No usable quote available for {sym} to compute a default limit price — pass limitPrice explicitly." });
                effectiveLimitPrice = Math.Round(quote.LastPrice * (1 + (profitPercent / 100m)), 2);
            }

            _logger.LogWarning("BrokerTest: PLACE TAKE-PROFIT {Symbol} x{Qty} @ {LimitPrice} (live={Live})", sym, Quantity, effectiveLimitPrice, live);
            var result = await broker.PlaceTakeProfitOrderAsync(accountNumber, sym, Quantity, effectiveLimitPrice, ct);
            return Ok(new { live, quantity = Quantity, symbol = sym, limitPrice = effectiveLimitPrice, brokerResponse = ParseBrokerResponse(result) });
        }

        /// <summary>Closes the position: SELL market order for 1 share. Fails at the broker
        /// (raw error returned) if there's nothing open to sell — that's expected and fine
        /// for a test endpoint.</summary>
        [HttpPost("order/close")]
        public async Task<IActionResult> CloseOrder([FromQuery] string? symbol, [FromQuery] bool live = false, CancellationToken ct = default)
        {
            var invalid = ValidateLiveReady(live);
            if (invalid != null) return invalid;

            var sym = ResolveSymbol(symbol);
            var accountNumber = _options.Value.Trading.AccountNumber;
            var broker = ResolveBroker(live);

            _logger.LogWarning("BrokerTest: CLOSE (SELL MARKET) {Symbol} x{Qty} (live={Live})", sym, Quantity, live);
            var result = await broker.PlaceSellMarketOrderAsync(accountNumber, sym, Quantity, ct);
            return Ok(new { live, quantity = Quantity, symbol = sym, brokerResponse = ParseBrokerResponse(result) });
        }

        private IBroker ResolveBroker(bool live) => live ? _schwabBroker : _simulatedBroker;

        private IQuoteProvider ResolveQuoteProvider(bool live) => live ? _schwabQuoteProvider : _simulatedBroker;

        private string ResolveSymbol(string? symbol) =>
            string.IsNullOrWhiteSpace(symbol) ? _options.Value.TqqqWeekly.Symbol : symbol.Trim().ToUpperInvariant();

        private IActionResult? ValidateLiveReady(bool live)
        {
            if (!live) return null;
            var trading = _options.Value.Trading;
            if (string.IsNullOrWhiteSpace(trading.AccountNumber))
                return BadRequest(new { message = "live=true requires AppSettings:Trading:AccountNumber to be configured." });
            if (string.IsNullOrWhiteSpace(trading.SchwabTokenPath))
                return BadRequest(new { message = "live=true requires AppSettings:Trading:SchwabTokenPath to be configured." });
            return null;
        }

        private static object ParseBrokerResponse(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (JsonException)
            {
                return json; // broker returned something that wasn't JSON -- surface it as-is rather than failing the whole response
            }
        }
    }
}
