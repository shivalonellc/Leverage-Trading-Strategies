using LeverageTradingStrategies.Infrastructure.Options;

namespace LeverageTradingStrategies.Domain.Options
{
    public sealed class PayoffPoint
    {
        public decimal UnderlyingPrice { get; set; }
        public decimal PnL { get; set; }
    }

    /// <summary>Everything the dashboard's payoff chart needs: the classic payoff-AT-EXPIRATION
    /// curve (intrinsic value only) plus a "today" curve priced with the position's ACTUAL
    /// remaining time-to-expiration and current implied vol (via Black-Scholes) — the live,
    /// greeks-based curve the user asked for, distinct from the textbook expiration diagram.
    /// Both curves span the same underlying-price range so they can be drawn on one chart.</summary>
    public sealed class VerticalSpreadPayoff
    {
        public List<PayoffPoint> AtExpiration { get; set; } = new();
        public List<PayoffPoint> Today { get; set; } = new();
        public decimal MaxProfit { get; set; }
        public decimal MaxLoss { get; set; }               // negative
        public decimal BreakevenPrice { get; set; }
        public decimal CurrentUnderlyingPrice { get; set; }
        public decimal? CurrentMarkPnL { get; set; }        // actual mark P&L at the current spot, from real bid/ask (not the theoretical curve)

        /// <summary>Rough probability-of-profit estimate: 1 - |short leg delta|. Delta
        /// approximates the risk-neutral probability an option finishes in-the-money, and for a
        /// simple 2-leg credit spread "profit" is closely approximated by "short leg finishes
        /// OTM" — the same shorthand OptionStrat and most retail options tools use. Not a
        /// precise probability (ignores the long leg and the exact breakeven vs. short strike
        /// gap), just a fast, good-enough-for-a-stats-row estimate. Null if no delta was
        /// available for the short leg.</summary>
        public decimal? ProbabilityOfProfit { get; set; }
    }

    public interface IVerticalSpreadPricingService
    {
        /// <summary>Builds both payoff curves across a price range centered on the current
        /// underlying price. impliedVolatility should be the average of the two legs' IV off
        /// the live chain (or a single leg's IV if only one is available) — used only for the
        /// "Today" curve; the AtExpiration curve is pure intrinsic value and doesn't need it.
        /// shortLegDelta (signed, as reported by the chain) feeds the ProbabilityOfProfit
        /// estimate — pass null to omit it.</summary>
        VerticalSpreadPayoff BuildPayoff(
            OptionRight right, decimal shortStrike, decimal longStrike, decimal netCredit, int contracts,
            decimal underlyingPrice, double yearsToExpiry, double impliedVolatility,
            decimal? currentMarkPnL = null, decimal? shortLegDelta = null, double riskFreeRate = 0.04);

        /// <summary>Current cost to close (short leg mid minus long leg mid), the resulting
        /// unrealized P&amp;L against netCreditReceived, and the position's net delta (long leg
        /// delta minus short leg delta — signed, works the same for puts and calls).</summary>
        (decimal spreadMarkPrice, decimal unrealizedPnL, decimal? netDelta) ComputeMark(
            OptionContractDto shortLeg, OptionContractDto longLeg, decimal netCreditReceived, int contracts);
    }
}
