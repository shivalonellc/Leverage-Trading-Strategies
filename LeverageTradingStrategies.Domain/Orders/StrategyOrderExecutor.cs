using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LeverageTradingStrategies.Domain.Orders
{
    public class StrategyOrderExecutor : IStrategyOrderExecutor
    {
        private readonly IBroker _broker;
        private readonly IStrategyOrderRepository _orderRepository;
        private readonly IStrategyInstanceRepository _instanceRepository;
        private readonly ILogger<StrategyOrderExecutor> _logger;

        public StrategyOrderExecutor(
            IBroker broker,
            IStrategyOrderRepository orderRepository,
            IStrategyInstanceRepository instanceRepository,
            ILogger<StrategyOrderExecutor> logger)
        {
            _broker = broker;
            _orderRepository = orderRepository;
            _instanceRepository = instanceRepository;
            _logger = logger;
        }

        public async Task<bool> ExecuteAsync(StrategyInstanceRecord instance, TqqqWeeklyDecision decision, decimal referencePrice, string accountNumber, bool isSimulated, CancellationToken ct = default)
        {
            if (decision.Action == TqqqWeeklyActionType.None)
            {
                _logger.LogDebug("No action: {Reason}", decision.Reason);
                return true;
            }

            if (decision.Quantity <= 0)
            {
                _logger.LogWarning("{Action} decision for {Symbol} had non-positive quantity ({Qty}) — skipping order placement. Reason: {Reason}",
                    decision.Action, instance.Symbol, decision.Quantity, decision.Reason);
                return true;
            }

            var side = decision.Action == TqqqWeeklyActionType.SellAll ? StrategyOrderSide.Sell : StrategyOrderSide.Buy;

            var orderRecord = new StrategyOrderRecord
            {
                StrategyInstanceId = instance.Id,
                Symbol = instance.Symbol,
                ActionType = decision.Action.ToString(),
                Side = side,
                Quantity = decision.Quantity,
                Reason = decision.Reason,
                IsSimulated = isSimulated,
                RequestedPrice = referencePrice,
                SubmittedUtc = DateTime.UtcNow
            };
            long orderId = await _orderRepository.InsertSubmittedAsync(orderRecord, ct);

            _logger.LogInformation("{Action} {Symbol} x{Qty} (order #{OrderId}) — {Reason}",
                decision.Action, instance.Symbol, decision.Quantity, orderId, decision.Reason);

            try
            {
                string result = decision.Action switch
                {
                    TqqqWeeklyActionType.EnterLong => await _broker.PlaceBuyMarketOrderAsync(accountNumber, instance.Symbol, decision.Quantity, ct),
                    TqqqWeeklyActionType.AddToPosition => await _broker.PlaceBuyMarketOrderAsync(accountNumber, instance.Symbol, decision.Quantity, ct),
                    TqqqWeeklyActionType.SellAll => await _broker.PlaceSellMarketOrderAsync(accountNumber, instance.Symbol, decision.Quantity, ct),
                    _ => "{}"
                };

                var (brokerOrderId, brokerStatus, brokerReason) = ParseBrokerResponse(result);

                // The broker call not throwing does NOT mean the order is actually working --
                // Schwab returns an orderId synchronously as soon as an order is accepted FOR
                // PROCESSING, then rejects it moments later via an async risk check (e.g. a
                // price too far from market). SchwabBroker now polls for that and reports
                // status:"rejected"/"failed" in the JSON body without throwing -- catch that
                // here rather than blindly marking the order Filled.
                bool brokerRejected = brokerStatus != null &&
                    (string.Equals(brokerStatus, "rejected", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(brokerStatus, "failed", StringComparison.OrdinalIgnoreCase));

                if (brokerRejected)
                {
                    string failureMessage = brokerReason ?? $"Broker reported status '{brokerStatus}'";
                    await _orderRepository.MarkFailedAsync(orderId, failureMessage, ct);
                    _logger.LogError("Order placement REJECTED by broker for {Action} {Symbol} x{Qty} (order #{OrderId}): {Reason} — state may now be out of sync with the real broker position, investigate immediately",
                        decision.Action, instance.Symbol, decision.Quantity, orderId, failureMessage);
                    return false;
                }

                // v1 known simplification: fill price is treated as the same reference price
                // the strategy used for its decision (best-case-fill assumption, matches the
                // verified backtest's own documented assumption — see spec doc Section 8).
                // Upgrading to a true fill-confirmation round-trip via
                // IBroker.GetOrderFillPriceAsync is a good follow-up hardening step before
                // trading meaningful size live.
                decimal fillPrice = referencePrice;

                decimal? realizedPnl = decision.Action == TqqqWeeklyActionType.SellAll ? decision.EstimatedRealizedPnL : null;
                await _orderRepository.MarkFilledAsync(orderId, fillPrice, brokerOrderId, realizedPnl, ct);

                if (decision.Action == TqqqWeeklyActionType.SellAll && instance.CompoundingEnabled && realizedPnl.HasValue)
                {
                    decimal newCapital = instance.CurrentCapital + realizedPnl.Value;
                    await _instanceRepository.UpdateCurrentCapitalAsync(instance.Id, newCapital, ct);
                    instance.CurrentCapital = newCapital; // keep the caller's in-memory copy in sync for the rest of this tick
                    _logger.LogInformation("Compounding: realized P&L {Pnl:C} rolled into CurrentCapital, now {NewCapital:C}", realizedPnl.Value, newCapital);
                }

                return true;
            }
            catch (Exception ex)
            {
                // NOTE (v1 known gap): does not reconcile state against the broker's actual
                // position on failure. If an order is rejected after the strategy's own state
                // was already mutated optimistically, state and the real broker position can
                // drift out of sync until manually reconciled. Recommended hardening before
                // trading real size: fill confirmation + reconciliation against
                // IBroker.GetSymbolPositionAsync, same pattern as options-seller order
                // confirmation in MarketMatrixPreparer.
                await _orderRepository.MarkFailedAsync(orderId, ex.Message, ct);
                _logger.LogError(ex, "Order placement FAILED for {Action} {Symbol} x{Qty} (order #{OrderId}) — state may now be out of sync with the real broker position, investigate immediately",
                    decision.Action, instance.Symbol, decision.Quantity, orderId);
                return false;
            }
        }

        private static (string? orderId, string? status, string? reason) ParseBrokerResponse(string brokerJsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(brokerJsonResponse);
                var root = doc.RootElement;
                string? orderId = root.TryGetProperty("orderId", out var idProp) ? idProp.GetString() : null;
                string? status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
                string? reason =
                    root.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() :
                    root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() :
                    root.TryGetProperty("error", out var errProp) ? errProp.GetString() :
                    null;
                return (orderId, status, reason);
            }
            catch (JsonException)
            {
                // broker response wasn't JSON or didn't have the expected shape -- treat as
                // "unknown, not explicitly rejected" rather than failing the whole call
                return (null, null, null);
            }
        }
    }
}
