using LeverageTradingStrategies.Infrastructure.Helpers;
using LeverageTradingStrategies.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchwabApiCS;
using SchwabApiCS.Orders;
using System.Collections.Concurrent;
using System.Text.Json;
using static SchwabApiCS.SchwabApi.AccountInfo.SecuritiesAccount;

namespace LeverageTradingStrategies.Infrastructure.Brokers
{
    public class SchwabBroker : IBroker
    {
        private readonly ILogger<SchwabBroker> _logger;
        private readonly int _maxRetryAttempts;
        private readonly int _retryDelayMilliseconds;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolLocks =
            new(StringComparer.OrdinalIgnoreCase);
        private IServiceScopeFactory _serviceScopeFactory;

        public SchwabBroker(ILogger<SchwabBroker> logger, IServiceScopeFactory serviceScopeFactory)
        {
           
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));

            _maxRetryAttempts = 3;
            _retryDelayMilliseconds = 1000;
        }

        private static SemaphoreSlim GetSymbolLock(string symbol) =>
            _symbolLocks.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Global API structural wrapper providing resilience handling across endpoints.
        /// </summary>
        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> apiCallFunc, string operationDescription)
        {
            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                try
                {
                    return await apiCallFunc();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Attempt {Attempt} failed for: {Operation}.", attempt, operationDescription);
                    if (attempt == _maxRetryAttempts)
                    {
                        _logger.LogError("All attempts failed for {Operation}. Giving up.", operationDescription);
                        return default;
                    }
                    await Task.Delay(_retryDelayMilliseconds);
                }
            }
            return default;
        }


        /// <summary>
        /// Total account equity (liquidationValue — cash plus mark-to-market value of any
        /// open position), used by TqqqWeeklyStrategyService for all sizing math.
        /// </summary>
        public async Task<decimal> GetPortfolioValueAsync(string accountNumber, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(accountNumber))
                return 0m;

            return await ExecuteWithRetryAsync(async () =>
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var accountData = await _schwabApi.GetAccountAsync(accountNumber, true);
                var balances = accountData?.Data?.securitiesAccount?.currentBalances;
                return balances?.liquidationValue ?? 0m;
            }, $"Getting portfolio value for account {accountNumber}");
        }

        /// <summary>
        /// Looks up if a single specific position exists inside the account portfolio.
        /// </summary>
        public async Task<SymbolPositionInfo?> GetSymbolPositionAsync(string accountNumber, string symbol)
        {
            if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(symbol))
                return null;

            symbol = symbol.Trim().ToUpperInvariant();

            return await ExecuteWithRetryAsync(async () =>
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var accountData = await _schwabApi.GetAccountAsync(accountNumber, true);
                var positions = accountData?.Data?.securitiesAccount?.positions;


                if (positions != null && positions.Any())
                {
                    Position? position = positions.FirstOrDefault(
                        p => p.instrument.symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                    decimal longQuantity = position?.longQuantity ?? 0;
                    decimal shortQuantity = position?.shortQuantity ?? 0;
                    decimal averagePrice = position?.averagePrice ?? 0;
                    decimal marketValue = position?.marketValue ?? 0;
                    decimal unrealizedProfitLoss = position?.longOpenProfitLoss ?? 0;

                    decimal costBasis = averagePrice * longQuantity;

                    decimal unrealizedProfitLossPercent = costBasis > 0
                        ? (unrealizedProfitLoss / costBasis) * 100
                        : 0;
                 

                    decimal netQuantity = longQuantity - shortQuantity;
                    PositionSide side =
                        netQuantity > 0 ? PositionSide.Long :
                        netQuantity < 0 ? PositionSide.Short :
                        PositionSide.Flat;

                    bool hasPosition = netQuantity != 0;
                    var result = new SymbolPositionInfo
                    {
                        AccountInfo = new MyAccountInfo() { AccountNumber = accountNumber },
                        Symbol = symbol.ToUpperInvariant(),

                        LongQuantity = longQuantity,
                        ShortQuantity = shortQuantity,
                        AveragePrice = averagePrice,
                        Side =side,



                        MarketValue = marketValue,
                        CostBasis = costBasis,

                        UnrealizedProfitLoss = unrealizedProfitLoss,
                        UnrealizedProfitLossPercent = unrealizedProfitLossPercent,

                        AsOfUtc = DateTime.UtcNow
                    };

                    if (accountData != null && accountData.Data != null && accountData.Data.securitiesAccount != null)
                    {
                        if (accountData.Data.securitiesAccount.currentBalances != null)
                        {
                            result.AccountInfo.Totalcash = accountData.Data.securitiesAccount.currentBalances.liquidationValue;
                            result.AccountInfo.CashBalance = accountData.Data.securitiesAccount.currentBalances.cashBalance;
                            result.AccountInfo.NonMaginableBuyingPower= accountData.Data.securitiesAccount.currentBalances.buyingPowerNonMarginableTrade;
                            result.AccountInfo.MaintenanceRequirement = accountData.Data.securitiesAccount.currentBalances.maintenanceRequirement;
                            result.AccountInfo.BuyingPower = accountData.Data.securitiesAccount.currentBalances.buyingPower;
                            result.AccountInfo.DayTradingBuyingPower = accountData.Data.securitiesAccount.currentBalances.dayTradingBuyingPower;

                        }
                    }

                    return result;
                }
                else
                {
                    return null;
                }

            }, $"Finding position details for {symbol}");
        }

      
        /// <summary>
        /// Submits an immediate market-order buy package directly into the live broker book.
        /// </summary>
        public async Task<string> PlaceBuyMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not fetch asset type parameters for {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.MARKET,
                    Order.Session.NORMAL,
                    Order.Duration.DAY,
                    Order.Position.TO_OPEN,
                    quantity
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), symbol = symbol, quantity = quantity });
            }
            finally
            {
                symLock.Release();
            }
        }

        /// <summary>
        /// Submits a limit order directly pegged to the current mark price.
        /// </summary>
        public async Task<string> PlaceBuyLimitOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not verify pricing for execution target {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);
                decimal targetMarkPrice = pquote.Data.quote.mark;

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.LIMIT,
                    Order.Session.SEAMLESS,
                    Order.Duration.DAY,
                    Order.Position.TO_OPEN,
                    quantity,
                    price: targetMarkPrice
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), limitPrice = targetMarkPrice, symbol = symbol, quantity = quantity });
            }
            finally
            {
                symLock.Release();
            }
        }

        /// <summary>
        /// Opens a short position: TO_OPEN with a NEGATIVE quantity, which
        /// Order.OrderLeg.CalculateInstruction maps to SELL_SHORT for non-option assets
        /// (see SchwabApiCS/Orders/Order.cs) — the exact same OrderSingleAsync overload
        /// PlaceBuyMarketOrderAsync already uses for the TO_OPEN/positive-quantity BUY case,
        /// just with the quantity sign flipped. This is what makes it possible to trade a
        /// symbol directly both directions (e.g. SPY) instead of only via a separate
        /// leveraged bull/bear instrument pair.
        /// </summary>
        public async Task<string> PlaceShortSellMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not fetch asset type parameters for {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.MARKET,
                    Order.Session.NORMAL,
                    Order.Duration.DAY,
                    Order.Position.TO_OPEN,
                    -quantity // negative + TO_OPEN => SELL_SHORT
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), symbol = symbol, quantity = quantity });
            }
            finally
            {
                symLock.Release();
            }
        }

        /// <summary>
        /// Closes/covers a short position: TO_CLOSE with a POSITIVE quantity, which maps to
        /// BUY_TO_COVER (not a plain BUY) for non-option assets — the closing counterpart to
        /// PlaceShortSellMarketOrderAsync, same OrderSingleAsync shape PlaceSellMarketOrderAsync
        /// already uses for TO_CLOSE/negative-quantity SELL, just with the sign flipped.
        /// </summary>
        public async Task<string> PlaceBuyToCoverMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not verify asset metrics for {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.MARKET,
                    Order.Session.NORMAL,
                    Order.Duration.DAY,
                    Order.Position.TO_CLOSE,
                    quantity < 0 ? -quantity : quantity // positive + TO_CLOSE => BUY_TO_COVER
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), symbol = symbol, quantity = quantity });
            }
            finally
            {
                symLock.Release();
            }
        }

        /// <summary>
        /// Submits an immediate market-order sell execution package to exit a long position.
        /// </summary>
        public async Task<string> PlaceSellMarketOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not verify asset metrics for {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.MARKET,
                    Order.Session.NORMAL,
                    Order.Duration.DAY,
                    Order.Position.TO_CLOSE,
                    quantity < 0 ? quantity : quantity * -1
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), symbol = symbol, quantity = quantity });
            }
            finally
            {
                symLock.Release();
            }
        }

        /// <summary>
        /// Submits a limit sell order pegged to the current asset mark price.
        /// </summary>
        public async Task<string> PlaceSellLimitOrderAsync(string accountNumber, string symbol, int quantity, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not verify pricing for sell target {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);
                decimal targetMarkPrice = pquote.Data.quote.mark;

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.LIMIT,
                    Order.Session.SEAMLESS,
                    Order.Duration.DAY,
                    Order.Position.TO_CLOSE,
                    quantity,
                    price: targetMarkPrice
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), limitPrice = targetMarkPrice, symbol = symbol, quantity = quantity });
            }
            finally
            {
                symLock.Release();
            }
        }

        /// <summary>
        /// Submits a stop-loss order to close an existing long position when the trigger price is hit.
        /// </summary>
        public async Task<string> PlaceStopLossOrderAsync(string accountNumber, string symbol, int quantity, decimal stopPrice, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || stopPrice <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not verify asset metrics for {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);

                // Note: The underlying wrapper negates this quantity internally (-quantity) to represent a position close
                var result = await _schwabApi.OrderStopLossAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.Duration.GOOD_TILL_CANCEL, // Typically preferred for structural stop protection
                    Order.Session.NORMAL,
                    quantity,
                    stopPrice
                );


                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = result?.Message ?? "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), symbol = symbol, quantity = quantity, stopPrice = stopPrice });
            }
            finally
            {
                symLock.Release();
            }
        }
        /// <summary>
        /// Submits a limit SELL order at an explicit target price (GTC) to close an existing
        /// long position — the take-profit counterpart to PlaceStopLossOrderAsync. Unlike
        /// PlaceSellLimitOrderAsync, the limit price is caller-supplied, not pegged to mark.
        /// </summary>
        public async Task<string> PlaceTakeProfitOrderAsync(string accountNumber, string symbol, int quantity, decimal limitPrice, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || limitPrice <= 0 || string.IsNullOrWhiteSpace(accountNumber))
                return JsonSerializer.Serialize(new { error = "Invalid parameter payloads sent to execution core" });

            symbol = symbol.Trim().ToUpperInvariant();
            var symLock = GetSymbolLock(symbol);
            await symLock.WaitAsync(ct);

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();
                var pquote = await _schwabApi.GetQuoteAsync(symbol);
                if (pquote?.Data == null)
                    return JsonSerializer.Serialize(new { error = $"Could not verify asset metrics for {symbol}" });

                var assetType = Order.GetAssetType(pquote.Data.assetMainType);

                var result = await _schwabApi.OrderSingleAsync(
                    accountNumber,
                    symbol,
                    assetType,
                    Order.OrderType.LIMIT,
                    Order.Session.SEAMLESS,
                    Order.Duration.GOOD_TILL_CANCEL,
                    Order.Position.TO_CLOSE,
                    quantity,
                    price: limitPrice
                );

                if (result?.Data == null)
                    return JsonSerializer.Serialize(new { status = "failed", message = result?.Message ?? "Null structural response from Schwab routing engine" });

                return JsonSerializer.Serialize(new { status = "success", orderId = result.Data.Value.ToString(), symbol = symbol, quantity = quantity, limitPrice = limitPrice });
            }
            finally
            {
                symLock.Release();
            }
        }

        public async Task<string> GetEquityMarketStatus()
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();

            return await ExecuteWithRetryAsync(async () =>
            {
                var marketHours = await _schwabApi.GetMarketHoursAsync();
                var equity = marketHours?.RawData?.equity;
                if (equity == null)
                {
                    return "CLOSED";
                }

                (DateTime, DateTime)? regular = equity.sessionHours?.regularMarket?.Any() == true
                    ? (equity.sessionHours.regularMarket[0].start, equity.sessionHours.regularMarket[0].end)
                    : null;

                (DateTime, DateTime)? pre = equity.sessionHours?.preMarket?.Any() == true
                    ? (equity.sessionHours.preMarket[0].start, equity.sessionHours.preMarket[0].end)
                    : null;

                (DateTime, DateTime)? post = equity.sessionHours?.postMarket?.Any() == true
                    ? (equity.sessionHours.postMarket[0].start, equity.sessionHours.postMarket[0].end)
                    : null;

                return MarketHoursHelper.DetermineMarketStatus(equity.isOpen, regular, pre, post);
            }, "Getting equity market status") ?? "CLOSED";
        }

        /// <summary>
        /// Retrieves the real, actual executed average fill price for a previously-placed order,
        /// using the SDK's own OrderLegFillDetails aggregation (quantity-weighted average across
        /// all FILL execution legs). Returns null if the order can't be found or has no fills yet
        /// (e.g. still pending) — callers should fall back to a less-accurate price source in that
        /// case rather than failing outright.
        /// </summary>
        public async Task<decimal?> GetOrderFillPriceAsync(string accountNumber, string orderId, CancellationToken ct = default)
        {
            if (!long.TryParse(orderId, out var parsedOrderId))
            {
                _logger.LogWarning("Cannot look up fill price — orderId '{OrderId}' is not a valid numeric Schwab order ID", orderId);
                return null;
            }

            return await ExecuteWithRetryAsync(async () =>
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var _schwabApi = scope.ServiceProvider.GetRequiredService<SchwabApi>();

                var orderResult = await _schwabApi.GetOrderAsync(accountNumber, parsedOrderId);
                var order = orderResult?.Data;

                if (order == null || order.orderLegCollection == null || order.orderLegCollection.Count == 0)
                {
                    _logger.LogWarning("Could not retrieve order {OrderId} or it has no legs — no fill price available", orderId);
                    return (decimal?)null;
                }

                var fillDetails = order.orderLegFillDetails[0];

                if (fillDetails.Quantity == 0)
                {
                    _logger.LogWarning("Order {OrderId} has no filled quantity yet (status={Status}) — no fill price available", orderId, order.status);
                    return (decimal?)null;
                }

                return fillDetails.AveragePrice;
            }, $"Getting fill price for order {orderId}");
        }

    }

  
}