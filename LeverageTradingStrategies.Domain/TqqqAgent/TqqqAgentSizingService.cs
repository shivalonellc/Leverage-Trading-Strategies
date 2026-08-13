namespace LeverageTradingStrategies.Domain.TqqqAgent
{
    /// <summary>Spec §8 sizing formula, pulled out into its own tiny pure-math class rather than
    /// inlined in the validator or the job -- easy to unit-test the arithmetic in isolation
    /// (e.g. the small-account "rounds to 0 shares" edge case) without any other module involved.</summary>
    public class TqqqAgentSizingService : ITqqqAgentSizingService
    {
        public int ComputeBuyShares(decimal availableCash, decimal currentPrice, decimal equityUsageFraction, decimal maxNotionalCeiling)
        {
            if (currentPrice <= 0 || availableCash <= 0)
                return 0;

            var maxNotional = Math.Min(availableCash * equityUsageFraction, maxNotionalCeiling);
            return (int)Math.Floor(maxNotional / currentPrice);
        }
    }
}
