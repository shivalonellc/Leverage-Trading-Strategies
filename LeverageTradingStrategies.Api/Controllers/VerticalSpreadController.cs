using LeverageTradingStrategies.Domain.Options;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
using LeverageTradingStrategies.Infrastructure.Options;
using LeverageTradingStrategies.Infrastructure.Quotes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeverageTradingStrategies.Api.Controllers
{
    public record BuildSpreadRequest(string Symbol, string Right, DateTime ExpirationDate, decimal ShortStrike, decimal LongStrike, int Contracts);

    /// <summary>
    /// Manual vertical-credit-spread builder: build (preview, no persistence), save (persist as
    /// Paper -- starts real-data mark-to-market tracking, no broker order), deploy (real Schwab
    /// combo order, Paper -> Live), close. Option chain/greeks come from Tradier
    /// (ITradierOptionsProvider); underlying spot from the concrete SchwabQuoteProvider (real
    /// data always, independent of AppSettings:Trading:UseSimulatedBroker -- see
    /// VerticalSpreadMarkingJob remarks for why); order EXECUTION always goes through Schwab,
    /// with a per-request "live" flag (default false = SimulatedBroker) on the endpoints that
    /// place real orders, same pattern as BrokerTestController.
    /// </summary>
    [ApiController]
    [Route("api/vertical-spread")]
    public class VerticalSpreadController : ControllerBase
    {
        private readonly ITradierOptionsProvider _optionsProvider;
        private readonly SchwabQuoteProvider _quoteProvider;
        private readonly IVerticalSpreadPricingService _pricingService;
        private readonly IVerticalSpreadOrderExecutor _orderExecutor;
        private readonly IVerticalSpreadRepository _repository;
        private readonly SchwabBroker _schwabBroker;
        private readonly SimulatedBroker _simulatedBroker;
        private readonly IOptions<AppSettingsOptions> _options;
        private readonly ILogger<VerticalSpreadController> _logger;

        public VerticalSpreadController(
            ITradierOptionsProvider optionsProvider,
            SchwabQuoteProvider quoteProvider,
            IVerticalSpreadPricingService pricingService,
            IVerticalSpreadOrderExecutor orderExecutor,
            IVerticalSpreadRepository repository,
            SchwabBroker schwabBroker,
            SimulatedBroker simulatedBroker,
            IOptions<AppSettingsOptions> options,
            ILogger<VerticalSpreadController> logger)
        {
            _optionsProvider = optionsProvider;
            _quoteProvider = quoteProvider;
            _pricingService = pricingService;
            _orderExecutor = orderExecutor;
            _repository = repository;
            _schwabBroker = schwabBroker;
            _simulatedBroker = simulatedBroker;
            _options = options;
            _logger = logger;
        }

        [HttpGet("expirations")]
        public async Task<IActionResult> GetExpirations([FromQuery] string symbol, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return BadRequest(new { message = "symbol is required" });
            return Ok(await _optionsProvider.GetExpirationsAsync(symbol, ct));
        }

        [HttpGet("chain")]
        public async Task<IActionResult> GetChain([FromQuery] string symbol, [FromQuery] DateTime expiration, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return BadRequest(new { message = "symbol is required" });
            var chain = await _optionsProvider.GetOptionChainAsync(symbol, expiration, ct);
            return Ok(ChainJson(chain));
        }

        /// <summary>Prices a candidate spread off the live chain and returns the payoff curves —
        /// no persistence, safe to call as the user adjusts strikes in the builder UI.</summary>
        [HttpPost("preview")]
        public async Task<IActionResult> Preview([FromBody] BuildSpreadRequest request, CancellationToken ct)
        {
            var result = await BuildCandidateAsync(request, ct);
            if (!result.Success) return BadRequest(new { message = result.Error });

            return Ok(new
            {
                spreadType = result.SpreadType.ToString(),
                netCredit = result.NetCredit,
                maxRiskPerSpread = result.MaxRisk,
                shortLeg = LegSummary(result.ShortLeg!),
                longLeg = LegSummary(result.LongLeg!),
                payoff = result.Payoff
            });
        }

        /// <summary>Persists the candidate as a new Paper strategy — starts real-data
        /// mark-to-market tracking immediately (VerticalSpreadMarkingJob), no broker order
        /// placed anywhere.</summary>
        [HttpPost("save")]
        public async Task<IActionResult> Save([FromBody] BuildSpreadRequest request, CancellationToken ct)
        {
            var result = await BuildCandidateAsync(request, ct);
            if (!result.Success) return BadRequest(new { message = result.Error });

            var now = DateTime.UtcNow;
            var record = new VerticalSpreadStrategyRecord
            {
                Symbol = request.Symbol.Trim().ToUpperInvariant(),
                SpreadType = result.SpreadType,
                Right = Enum.Parse<OptionRight>(request.Right, true),
                ExpirationDate = request.ExpirationDate.Date,
                ShortStrike = request.ShortStrike,
                LongStrike = request.LongStrike,
                ShortOptionSymbol = result.ShortLeg!.Symbol,
                LongOptionSymbol = result.LongLeg!.Symbol,
                Contracts = request.Contracts,
                ShortDeltaAtBuild = result.ShortLeg.Delta,
                LongDeltaAtBuild = result.LongLeg.Delta,
                NetCreditAtBuild = result.NetCredit,
                MaxRiskPerSpread = result.MaxRisk,
                Status = VerticalSpreadStatus.Paper,
                OpenedUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            long id = await _repository.InsertAsync(record, ct);
            _logger.LogInformation("VerticalSpreadController: saved new Paper spread #{Id} {Symbol} {Type} short {Short}/long {Long} exp {Exp:yyyy-MM-dd} x{Contracts}",
                id, record.Symbol, record.SpreadType, record.ShortStrike, record.LongStrike, record.ExpirationDate, record.Contracts);

            return Ok(new { id, status = "Paper" });
        }

        /// <summary>Places the real (or, with live=false, simulated) opening combo order for a
        /// Paper strategy. THIS PLACES A REAL ORDER WITH REAL MONEY when live=true.</summary>
        [HttpPost("deploy/{id}")]
        public async Task<IActionResult> Deploy(long id, [FromQuery] bool live = false, CancellationToken ct = default)
        {
            var strategy = await _repository.GetByIdAsync(id, ct);
            if (strategy == null) return NotFound();
            if (strategy.Status != VerticalSpreadStatus.Paper)
                return BadRequest(new { message = $"Strategy #{id} is {strategy.Status}, not Paper — nothing to deploy." });

            var invalid = ValidateLiveReady(live);
            if (invalid != null) return invalid;

            _logger.LogWarning("VerticalSpreadController: DEPLOY requested for spread #{Id} (live={Live})", id, live);
            bool ok = await _orderExecutor.DeployAsync(strategy, ResolveBroker(live), _options.Value.Trading.AccountNumber, ct);
            var updated = await _repository.GetByIdAsync(id, ct);

            return ok
                ? Ok(new { success = true, strategy = updated == null ? null : StrategyJson(updated) })
                : StatusCode(StatusCodes.Status502BadGateway, new { success = false, strategy = updated == null ? null : StrategyJson(updated), message = "Deploy was rejected or failed — see this strategy's recent orders for detail. It stays Paper and is retryable." });
        }

        /// <summary>Closes a strategy (Paper or Live) at the current mark. THIS PLACES A REAL
        /// ORDER WITH REAL MONEY when the strategy is Live and live=true.</summary>
        [HttpPost("close/{id}")]
        public async Task<IActionResult> Close(long id, [FromQuery] bool live = false, [FromQuery] string? reason = null, CancellationToken ct = default)
        {
            var strategy = await _repository.GetByIdAsync(id, ct);
            if (strategy == null) return NotFound();
            if (strategy.Status != VerticalSpreadStatus.Paper && strategy.Status != VerticalSpreadStatus.Live)
                return BadRequest(new { message = $"Strategy #{id} is {strategy.Status} — nothing to close." });

            var chain = await _optionsProvider.GetOptionChainAsync(strategy.Symbol, strategy.ExpirationDate, ct);
            var shortLeg = chain.Options.FirstOrDefault(o => o.Symbol == strategy.ShortOptionSymbol);
            var longLeg = chain.Options.FirstOrDefault(o => o.Symbol == strategy.LongOptionSymbol);
            if (shortLeg == null || longLeg == null)
                return BadRequest(new { message = "Could not find both legs in the current chain to price the close — try again once the chain is available." });

            var (spreadMarkPrice, _, _) = _pricingService.ComputeMark(shortLeg, longLeg, strategy.NetCreditReceived ?? strategy.NetCreditAtBuild, strategy.Contracts);

            IBroker broker;
            if (strategy.Status == VerticalSpreadStatus.Live)
            {
                var invalid = ValidateLiveReady(live);
                if (invalid != null) return invalid;
                broker = ResolveBroker(live);
            }
            else
            {
                broker = _simulatedBroker; // Paper close never reaches the broker (short-circuited in the executor) — any IBroker works
            }

            _logger.LogWarning("VerticalSpreadController: CLOSE requested for spread #{Id} (live={Live}, reason={Reason})", id, live, reason ?? "manual");
            bool ok = await _orderExecutor.CloseAsync(strategy, broker, _options.Value.Trading.AccountNumber, reason ?? "Manually closed", spreadMarkPrice, ct);
            var updated = await _repository.GetByIdAsync(id, ct);

            return ok
                ? Ok(new { success = true, strategy = updated == null ? null : StrategyJson(updated) })
                : StatusCode(StatusCodes.Status502BadGateway, new { success = false, strategy = updated == null ? null : StrategyJson(updated), message = "Close was rejected or failed — see this strategy's recent orders for detail." });
        }

        [HttpGet("list")]
        public async Task<IActionResult> List(CancellationToken ct) =>
            Ok((await _repository.GetAllAsync(ct)).Select(StrategyJson));

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(long id, CancellationToken ct)
        {
            var strategy = await _repository.GetByIdAsync(id, ct);
            return strategy == null ? NotFound() : Ok(StrategyJson(strategy));
        }

        [HttpGet("{id}/orders")]
        public async Task<IActionResult> GetOrders(long id, CancellationToken ct) =>
            Ok((await _repository.GetOrdersAsync(id, ct)).Select(OrderJson));

        [HttpGet("{id}/marks")]
        public async Task<IActionResult> GetMarks(long id, [FromQuery] int limit = 500, CancellationToken ct = default) => Ok(await _repository.GetMarksAsync(id, limit, ct));

        /// <summary>Live payoff for an already-saved strategy: the payoff-at-expiration curve,
        /// the "today" theoretical curve (real remaining DTE + current chain IV), and the
        /// actual current mark P&amp;L from live bid/ask — everything the dashboard's payoff
        /// chart draws.</summary>
        [HttpGet("{id}/payoff")]
        public async Task<IActionResult> GetPayoff(long id, CancellationToken ct)
        {
            var strategy = await _repository.GetByIdAsync(id, ct);
            if (strategy == null) return NotFound();

            var chain = await _optionsProvider.GetOptionChainAsync(strategy.Symbol, strategy.ExpirationDate, ct);
            var shortLeg = chain.Options.FirstOrDefault(o => o.Symbol == strategy.ShortOptionSymbol);
            var longLeg = chain.Options.FirstOrDefault(o => o.Symbol == strategy.LongOptionSymbol);
            var quote = await _quoteProvider.GetQuoteAsync(strategy.Symbol, ct);
            decimal underlyingPrice = quote?.LastPrice ?? 0m;

            decimal? currentMarkPnL = null;
            double iv = 0.30; // sane fallback if the chain has no usable IV right now (e.g. after hours)
            if (shortLeg != null && longLeg != null)
            {
                var (_, unrealizedPnL, _) = _pricingService.ComputeMark(shortLeg, longLeg, strategy.NetCreditReceived ?? strategy.NetCreditAtBuild, strategy.Contracts);
                currentMarkPnL = unrealizedPnL;
                double avgIv = (double)(((shortLeg.ImpliedVolatility ?? 0m) + (longLeg.ImpliedVolatility ?? 0m)) / 2m);
                if (avgIv > 0) iv = avgIv;
            }

            double yearsToExpiry = Math.Max(0.0, (strategy.ExpirationDate.Date - DateTime.UtcNow.Date).TotalDays) / 365.0;
            var payoff = _pricingService.BuildPayoff(
                strategy.Right, strategy.ShortStrike, strategy.LongStrike,
                strategy.NetCreditReceived ?? strategy.NetCreditAtBuild, strategy.Contracts,
                underlyingPrice, yearsToExpiry, iv, currentMarkPnL);

            return Ok(payoff);
        }

        private async Task<SpreadBuildResult> BuildCandidateAsync(BuildSpreadRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Symbol))
                return SpreadBuildResult.Fail("Symbol is required.");
            if (!Enum.TryParse<OptionRight>(request.Right, true, out var right))
                return SpreadBuildResult.Fail($"Invalid Right '{request.Right}' — must be 'Put' or 'Call'.");
            if (request.Contracts <= 0)
                return SpreadBuildResult.Fail("Contracts must be positive.");

            VerticalSpreadType spreadType;
            if (right == OptionRight.Put)
            {
                if (request.ShortStrike <= request.LongStrike)
                    return SpreadBuildResult.Fail("Bull put credit spread: ShortStrike must be greater than LongStrike (short leg closer to the money, long leg further OTM/lower, as protection).");
                spreadType = VerticalSpreadType.BullPutCredit;
            }
            else
            {
                if (request.ShortStrike >= request.LongStrike)
                    return SpreadBuildResult.Fail("Bear call credit spread: ShortStrike must be less than LongStrike (short leg closer to the money, long leg further OTM/higher, as protection).");
                spreadType = VerticalSpreadType.BearCallCredit;
            }

            var symbol = request.Symbol.Trim().ToUpperInvariant();
            var chain = await _optionsProvider.GetOptionChainAsync(symbol, request.ExpirationDate, ct);
            var shortLeg = chain.Options.FirstOrDefault(o => o.Right == right && o.Strike == request.ShortStrike);
            var longLeg = chain.Options.FirstOrDefault(o => o.Right == right && o.Strike == request.LongStrike);
            if (shortLeg == null || longLeg == null)
                return SpreadBuildResult.Fail($"Could not find both strikes in the {symbol} {request.ExpirationDate:yyyy-MM-dd} chain (short {request.ShortStrike}, long {request.LongStrike}).");
            if (!shortLeg.HasValidBidAsk || !longLeg.HasValidBidAsk)
                return SpreadBuildResult.Fail("One or both legs have no valid bid/ask right now — try again when the market is open.");

            decimal netCredit = shortLeg.Bid - longLeg.Ask; // conservative marketable pricing, matches OptionsSellerStrategyService's own preview convention
            if (netCredit <= 0)
                return SpreadBuildResult.Fail($"Quoted net credit is not positive (short bid {shortLeg.Bid}, long ask {longLeg.Ask}) — this would open at a debit, not a credit.");

            decimal width = Math.Abs(request.ShortStrike - request.LongStrike);
            decimal maxRisk = (width - netCredit) * request.Contracts * 100m;

            var quote = await _quoteProvider.GetQuoteAsync(symbol, ct);
            decimal underlyingPrice = quote?.LastPrice ?? 0m;

            double yearsToExpiry = Math.Max(0.0, (request.ExpirationDate.Date - DateTime.UtcNow.Date).TotalDays) / 365.0;
            double iv = (double)(((shortLeg.ImpliedVolatility ?? 0m) + (longLeg.ImpliedVolatility ?? 0m)) / 2m);
            if (iv <= 0) iv = 0.30;

            var payoff = _pricingService.BuildPayoff(right, request.ShortStrike, request.LongStrike, netCredit, request.Contracts, underlyingPrice, yearsToExpiry, iv);

            return SpreadBuildResult.Ok(shortLeg, longLeg, spreadType, netCredit, maxRisk, payoff);
        }

        private static object LegSummary(OptionContractDto leg) => new { leg.Symbol, leg.Strike, leg.Bid, leg.Ask, leg.Mid, leg.Delta, leg.ImpliedVolatility, leg.OpenInterest, leg.Volume };

        // This project has no global JsonStringEnumConverter configured (see TqqqWeeklyController,
        // which explicitly calls .ToString() on every enum it returns for the same reason) --
        // returning a raw record with an enum-typed property would serialize that field as a bare
        // integer, not a string, silently breaking any client-side string comparison. These
        // projections are the vertical-spread module's equivalent of that same explicit pattern.

        private static object StrategyJson(VerticalSpreadStrategyRecord s) => new
        {
            s.Id,
            s.Symbol,
            SpreadType = s.SpreadType.ToString(),
            Right = s.Right.ToString(),
            s.ExpirationDate,
            s.ShortStrike,
            s.LongStrike,
            s.ShortOptionSymbol,
            s.LongOptionSymbol,
            s.Contracts,
            s.ShortDeltaAtBuild,
            s.LongDeltaAtBuild,
            s.NetCreditAtBuild,
            s.MaxRiskPerSpread,
            Status = s.Status.ToString(),
            s.NetCreditReceived,
            s.OpenedUtc,
            s.DeployedUtc,
            s.ClosedUtc,
            s.RealizedPnL,
            s.CloseReason,
            s.CreatedUtc,
            s.UpdatedUtc,
            s.Width
        };

        private static object OrderJson(VerticalSpreadOrderRecord o) => new
        {
            o.Id,
            o.VerticalSpreadStrategyId,
            ActionType = o.ActionType.ToString(),
            o.LongOptionSymbol,
            o.ShortOptionSymbol,
            o.Contracts,
            o.RequestedPrice,
            o.FillPrice,
            Status = o.Status.ToString(),
            o.BrokerOrderId,
            o.ErrorMessage,
            o.SubmittedUtc,
            o.FilledUtc
        };

        private static object ChainJson(OptionChainDto chain) => new
        {
            chain.UnderlyingSymbol,
            chain.ExpirationDate,
            chain.RetrievedAtUtc,
            options = chain.Options.Select(o => new
            {
                o.Symbol,
                o.UnderlyingSymbol,
                o.ExpirationDate,
                Right = o.Right.ToString(),
                o.Strike,
                o.Bid,
                o.Ask,
                o.Last,
                o.Volume,
                o.OpenInterest,
                o.Delta,
                o.Gamma,
                o.Theta,
                o.Vega,
                o.ImpliedVolatility,
                o.Mid,
                o.HasValidBidAsk
            })
        };

        private IBroker ResolveBroker(bool live) => live ? _schwabBroker : _simulatedBroker;

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

        private class SpreadBuildResult
        {
            public bool Success { get; private init; }
            public string? Error { get; private init; }
            public OptionContractDto? ShortLeg { get; private init; }
            public OptionContractDto? LongLeg { get; private init; }
            public VerticalSpreadType SpreadType { get; private init; }
            public decimal NetCredit { get; private init; }
            public decimal MaxRisk { get; private init; }
            public VerticalSpreadPayoff? Payoff { get; private init; }

            public static SpreadBuildResult Fail(string error) => new() { Success = false, Error = error };

            public static SpreadBuildResult Ok(OptionContractDto shortLeg, OptionContractDto longLeg, VerticalSpreadType spreadType, decimal netCredit, decimal maxRisk, VerticalSpreadPayoff payoff) =>
                new() { Success = true, ShortLeg = shortLeg, LongLeg = longLeg, SpreadType = spreadType, NetCredit = netCredit, MaxRisk = maxRisk, Payoff = payoff };
        }
    }
}
