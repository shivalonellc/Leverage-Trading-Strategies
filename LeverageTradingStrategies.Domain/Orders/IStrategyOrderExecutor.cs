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
        /// SimulatedBroker — isSimulated only controls what gets recorded on the order row.
        ///
        /// Returns true if nothing needed doing (Action == None, or a non-positive quantity)
        /// OR the order placed and filled successfully; returns false only when the order was
        /// actually submitted to the broker and the broker call itself failed (marked Failed).
        /// The kill-switch endpoint uses this to decide whether it's safe to mark the instance
        /// Killed — a false return means the position was NOT confirmed squared off.</summary>
        Task<bool> ExecuteAsync(StrategyInstanceRecord instance, TqqqWeeklyDecision decision, decimal referencePrice, string accountNumber, bool isSimulated, CancellationToken ct = default);
    }
}
