using LeverageTradingStrategies.Infrastructure.Brokers;
using LeverageTradingStrategies.Infrastructure.Data.Entities;

namespace LeverageTradingStrategies.Domain.Options
{
    public interface IVerticalSpreadOrderExecutor
    {
        /// <summary>Places the real (or simulated) opening combo order for a Paper strategy and,
        /// on a confirmed fill, transitions it to Live. Does NOT touch Status on rejection/
        /// failure — the strategy stays Paper and retryable. Returns false on any non-fill
        /// outcome (rejected, failed, or a broker exception). The broker is passed explicitly
        /// (not resolved from DI) so a caller can target either SimulatedBroker or the real
        /// SchwabBroker per-request, same "live" flag pattern BrokerTestController uses.</summary>
        Task<bool> DeployAsync(VerticalSpreadStrategyRecord strategy, IBroker broker, string accountNumber, CancellationToken ct = default);

        /// <summary>Places the closing combo order (works for both Paper and Live — a Paper
        /// close just settles against the current mark instead of touching a real order) and,
        /// on success, transitions the strategy to Closed with RealizedPnL populated.</summary>
        Task<bool> CloseAsync(VerticalSpreadStrategyRecord strategy, IBroker broker, string accountNumber, string reason, decimal closeDebitOrCredit, CancellationToken ct = default);
    }
}
