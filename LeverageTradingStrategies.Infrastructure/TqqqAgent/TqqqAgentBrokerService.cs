using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public class TqqqAgentBrokerService : ITqqqAgentBrokerService
    {
        private const string Symbol = "TQQQ";
        private static readonly TimeSpan FillPollInterval = TimeSpan.FromMilliseconds(750);
        private const int FillPollAttempts = 6; // ~4.5s total -- generous for a liquid ETF market order, still small next to the 5-minute cycle

        private readonly ITradierAccountRestClient _client;
        private readonly ILogger<TqqqAgentBrokerService> _logger;

        public TqqqAgentBrokerService(ITradierAccountRestClient client, ILogger<TqqqAgentBrokerService> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task<TqqqAgentPortfolioSnapshot> GetPortfolioSnapshotAsync(
            decimal dayStartEquity,
            decimal realizedPnLToday,
            int consecutiveLossesToday,
            bool haltActive,
            string? haltReason,
            CancellationToken ct = default)
        {
            var balancesTask = _client.GetBalancesAsync(ct);
            var positionsTask = _client.GetPositionsAsync(ct);
            await Task.WhenAll(balancesTask, positionsTask);

            var balances = balancesTask.Result;
            var position = positionsTask.Result.FirstOrDefault(p => p.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase) && p.Quantity != 0);

            var holding = position != null;
            var quantity = position != null ? (int)position.Quantity : 0;
            var entryPrice = position != null && position.Quantity != 0 ? Math.Round(position.CostBasis / position.Quantity, 3, MidpointRounding.AwayFromZero) : (decimal?)null;

            return new TqqqAgentPortfolioSnapshot
            {
                Holding = holding,
                Quantity = quantity,
                EntryPrice = entryPrice,
                CashAvailable = balances.TotalCash,
                TotalEquity = balances.TotalEquity,
                DayStartEquity = dayStartEquity,
                RealizedPnLToday = realizedPnLToday,
                ConsecutiveLossesToday = consecutiveLossesToday,
                HaltActive = haltActive,
                HaltReason = haltReason
            };
        }

        public async Task<TqqqAgentOrderResult> PlaceOrderAsync(string side, int quantity, CancellationToken ct = default)
        {
            if (quantity <= 0)
                return new TqqqAgentOrderResult { Success = false, Status = "Failed", ErrorMessage = $"Refusing to place an order for non-positive quantity ({quantity})." };

            var (placed, orderId, placeError) = await _client.PlaceMarketOrderAsync(Symbol, side, quantity, ct);
            if (!placed || orderId is null)
            {
                _logger.LogError("TqqqAgentBrokerService: order placement failed for {Side} {Qty} {Symbol}: {Error}", side, quantity, Symbol, placeError);
                return new TqqqAgentOrderResult { Success = false, Status = "Failed", ErrorMessage = placeError ?? "Order placement failed with no error detail." };
            }

            for (var attempt = 0; attempt < FillPollAttempts; attempt++)
            {
                var order = await _client.GetOrderAsync(orderId.Value, ct);
                if (order == null)
                    break;

                switch (order.Status.ToLowerInvariant())
                {
                    case "filled":
                        return new TqqqAgentOrderResult
                        {
                            Success = true,
                            Status = "Filled",
                            BrokerOrderId = orderId.Value.ToString(),
                            FillPrice = order.AvgFillPrice,
                            FilledQuantity = (int)(order.ExecQuantity ?? quantity)
                        };
                    case "rejected":
                    case "canceled":
                    case "expired":
                        _logger.LogError("TqqqAgentBrokerService: order {OrderId} ended in status {Status}", orderId, order.Status);
                        return new TqqqAgentOrderResult { Success = false, Status = "Failed", BrokerOrderId = orderId.Value.ToString(), ErrorMessage = $"Order ended in status '{order.Status}'." };
                }

                if (attempt < FillPollAttempts - 1)
                    await Task.Delay(FillPollInterval, ct);
            }

            // Still open/pending after the poll window -- not a failure, just not confirmed
            // filled yet. The job records this as Submitted; a later cycle's portfolio read from
            // live broker state is the actual source of truth regardless.
            _logger.LogWarning("TqqqAgentBrokerService: order {OrderId} not confirmed filled within the poll window -- recording as Submitted.", orderId);
            return new TqqqAgentOrderResult { Success = true, Status = "Submitted", BrokerOrderId = orderId.Value.ToString() };
        }
    }
}
