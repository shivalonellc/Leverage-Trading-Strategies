using LeverageTradingStrategies.Domain.Tqqq;
using LeverageTradingStrategies.Infrastructure.Data.Entities;

namespace LeverageTradingStrategies.Domain.Orders
{
    public interface IStrategyOrderExecutor
    {
        /// <summary>Executes a strategy decision: records a StrategyOrder row (Submitted),
        /// places the order via IBroker, then marks the order Filled/Failed. On a filled
        /// SellAll, also rolls the realized P&L into the instance's CurrentCapital if
        /// compounding is enabled. Used by BOTH the live job and the kill-switch controller
        /// endpoint, and works identically whether IBroker resolves to SchwabBroker or
        /// SimulatedBroker — isSimulated only controls what gets recorded on the order row.</summary>
        Task ExecuteAsync(StrategyInstanceRecord instance, TqqqWeeklyDecision decision, decimal referencePrice, string accountNumber, bool isSimulated, CancellationToken ct = default);
    }
}
