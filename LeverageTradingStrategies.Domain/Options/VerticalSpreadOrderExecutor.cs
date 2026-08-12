using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Data.Entities;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LeverageTradingStrategies.Domain.Options
{
    /// <summary>
    /// The single code path that places a vertical spread's real combo order (open or close)
    /// and records the outcome — mirrors StrategyOrderExecutor's Submitted->Filled/Failed shape
    /// and, critically, the SAME broker-response status parsing that fix taught: a combo order
    /// call not throwing does NOT mean it filled (Schwab's async risk check can still reject it
    /// after returning an orderId) — SchwabBroker's PlaceVerticalCreditSpread*OrderAsync already
    /// reports status:"rejected"/"failed" in its JSON for that case, and this class honors it
    /// rather than blindly marking the order Filled and transitioning strategy status.
    /// </summary>
    public class VerticalSpreadOrderExecutor : IVerticalSpreadOrderExecutor
    {
        private readonly IVerticalSpreadRepository _repository;
        private readonly ILogger<VerticalSpreadOrderExecutor> _logger;

        public VerticalSpreadOrderExecutor(IVerticalSpreadRepository repository, ILogger<VerticalSpreadOrderExecutor> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public async Task<bool> DeployAsync(VerticalSpreadStrategyRecord strategy, IBroker broker, string accountNumber, CancellationToken ct = default)
        {
            var orderRecord = new VerticalSpreadOrderRecord
            {
                VerticalSpreadStrategyId = strategy.Id,
                ActionType = VerticalSpreadOrderAction.Open,
                LongOptionSymbol = strategy.LongOptionSymbol,
                ShortOptionSymbol = strategy.ShortOptionSymbol,
                Contracts = strategy.Contracts,
                RequestedPrice = strategy.NetCreditAtBuild,
                SubmittedUtc = DateTime.UtcNow
            };
            long orderId = await _repository.InsertOrderAsync(orderRecord, ct);

            _logger.LogWarning("VerticalSpreadOrderExecutor: DEPLOY spread #{StrategyId} {Symbol} short {Short}/long {Long} x{Contracts} @ net credit {Credit} (broker={Broker})",
                strategy.Id, strategy.Symbol, strategy.ShortStrike, strategy.LongStrike, strategy.Contracts, strategy.NetCreditAtBuild, broker.GetType().Name);

            try
            {
                string result = await broker.PlaceVerticalCreditSpreadOpenOrderAsync(
                    accountNumber, strategy.LongOptionSymbol, strategy.ShortOptionSymbol, strategy.Contracts, strategy.NetCreditAtBuild, ct);

                var (brokerOrderId, status, reason) = ParseBrokerResponse(result);
                bool rejected = status != null && (Eq(status, "rejected") || Eq(status, "failed"));

                if (rejected)
                {
                    string failureMessage = reason ?? $"Broker reported status '{status}'";
                    await _repository.MarkOrderRejectedAsync(orderId, failureMessage, ct);
                    await _repository.MarkFailedAsync(strategy.Id, ct);
                    _logger.LogError("VerticalSpreadOrderExecutor: DEPLOY REJECTED for spread #{StrategyId} — {Reason}. Strategy stays Paper (retryable), nothing opened at the broker.", strategy.Id, failureMessage);
                    return false;
                }

                await _repository.MarkOrderFilledAsync(orderId, strategy.NetCreditAtBuild, brokerOrderId, ct);
                await _repository.MarkDeployedAsync(strategy.Id, strategy.NetCreditAtBuild, ct);
                _logger.LogInformation("VerticalSpreadOrderExecutor: spread #{StrategyId} DEPLOYED — now Live", strategy.Id);
                return true;
            }
            catch (Exception ex)
            {
                await _repository.MarkOrderFailedAsync(orderId, ex.Message, ct);
                await _repository.MarkFailedAsync(strategy.Id, ct);
                _logger.LogError(ex, "VerticalSpreadOrderExecutor: DEPLOY threw for spread #{StrategyId} — strategy stays Paper (retryable)", strategy.Id);
                return false;
            }
        }

        public async Task<bool> CloseAsync(VerticalSpreadStrategyRecord strategy, IBroker broker, string accountNumber, string reason, decimal closeDebitOrCredit, CancellationToken ct = default)
        {
            var orderRecord = new VerticalSpreadOrderRecord
            {
                VerticalSpreadStrategyId = strategy.Id,
                ActionType = VerticalSpreadOrderAction.Close,
                LongOptionSymbol = strategy.LongOptionSymbol,
                ShortOptionSymbol = strategy.ShortOptionSymbol,
                Contracts = strategy.Contracts,
                RequestedPrice = closeDebitOrCredit,
                SubmittedUtc = DateTime.UtcNow
            };
            long orderId = await _repository.InsertOrderAsync(orderRecord, ct);

            decimal netCreditReceived = strategy.NetCreditReceived ?? strategy.NetCreditAtBuild;

            // Paper positions never touched the broker on open, so closing one doesn't touch it
            // either -- settle the trade against the current mark/debit directly.
            if (strategy.Status == VerticalSpreadStatus.Paper)
            {
                decimal realizedPnl = (netCreditReceived - closeDebitOrCredit) * strategy.Contracts * 100m;
                await _repository.MarkOrderFilledAsync(orderId, closeDebitOrCredit, null, ct);
                await _repository.MarkClosedAsync(strategy.Id, realizedPnl, reason, ct);
                _logger.LogInformation("VerticalSpreadOrderExecutor: paper spread #{StrategyId} closed — {Reason}, realized P&L {Pnl:C}", strategy.Id, reason, realizedPnl);
                return true;
            }

            _logger.LogWarning("VerticalSpreadOrderExecutor: CLOSE spread #{StrategyId} {Symbol} at net debit {Debit} — {Reason} (broker={Broker})",
                strategy.Id, strategy.Symbol, closeDebitOrCredit, reason, broker.GetType().Name);

            try
            {
                string result = await broker.PlaceVerticalCreditSpreadCloseOrderAsync(
                    accountNumber, strategy.LongOptionSymbol, strategy.ShortOptionSymbol, strategy.Contracts, closeDebitOrCredit, ct);

                var (brokerOrderId, status, brokerReason) = ParseBrokerResponse(result);
                bool rejected = status != null && (Eq(status, "rejected") || Eq(status, "failed"));

                if (rejected)
                {
                    string failureMessage = brokerReason ?? $"Broker reported status '{status}'";
                    await _repository.MarkOrderRejectedAsync(orderId, failureMessage, ct);
                    _logger.LogError("VerticalSpreadOrderExecutor: CLOSE REJECTED for spread #{StrategyId} — {Reason}. Strategy stays Live — retry the close manually.", strategy.Id, failureMessage);
                    return false;
                }

                decimal realizedPnl = (netCreditReceived - closeDebitOrCredit) * strategy.Contracts * 100m;
                await _repository.MarkOrderFilledAsync(orderId, closeDebitOrCredit, brokerOrderId, ct);
                await _repository.MarkClosedAsync(strategy.Id, realizedPnl, reason, ct);
                _logger.LogInformation("VerticalSpreadOrderExecutor: spread #{StrategyId} CLOSED — {Reason}, realized P&L {Pnl:C}", strategy.Id, reason, realizedPnl);
                return true;
            }
            catch (Exception ex)
            {
                await _repository.MarkOrderFailedAsync(orderId, ex.Message, ct);
                _logger.LogError(ex, "VerticalSpreadOrderExecutor: CLOSE threw for spread #{StrategyId} — strategy stays Live, position may need manual attention", strategy.Id);
                return false;
            }
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
                    null;
                return (orderId, status, reason);
            }
            catch (JsonException)
            {
                return (null, null, null);
            }
        }
    }
}
