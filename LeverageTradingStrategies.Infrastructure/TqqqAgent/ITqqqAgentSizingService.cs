namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public interface ITqqqAgentSizingService
    {
        /// <summary>Spec §8: maxNotional = min(availableCash * EquityUsageFraction,
        /// MaxNotionalCeiling); shares = floor(maxNotional / currentPrice). Returns 0 (not an
        /// exception) if the computed size rounds down to nothing -- the validator's check 7
        /// handles that case, this method just does the arithmetic.</summary>
        int ComputeBuyShares(decimal availableCash, decimal currentPrice, decimal equityUsageFraction, decimal maxNotionalCeiling);
    }
}
