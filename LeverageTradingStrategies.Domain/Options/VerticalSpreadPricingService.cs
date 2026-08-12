using LeverageTradingStrategies.Infrastructure.Options;

namespace LeverageTradingStrategies.Domain.Options
{
    /// <summary>
    /// Pure payoff/mark math for a vertical credit spread. Both spread types (bull put credit:
    /// short the higher put strike, long the lower put strike as protection; bear call credit:
    /// short the lower call strike, long the higher call strike as protection) reduce to the
    /// same signed formula: PnL = netCredit + intrinsicOrTheoretical(long) - intrinsicOrTheoretical(short),
    /// scaled by contracts*100 — the trader is short 1x the short leg (collects/owes its value)
    /// and long 1x the long leg (pays/receives its value), regardless of which right is used, as
    /// long as ShortStrike/LongStrike were assigned correctly at build time (short = premium-
    /// collecting leg, long = protective leg further OTM).
    /// </summary>
    public class VerticalSpreadPricingService : IVerticalSpreadPricingService
    {
        private const int PricePoints = 61;
        private const double PriceRangeFraction = 0.25; // ±25% around the current underlying price

        public VerticalSpreadPayoff BuildPayoff(
            OptionRight right, decimal shortStrike, decimal longStrike, decimal netCredit, int contracts,
            decimal underlyingPrice, double yearsToExpiry, double impliedVolatility,
            decimal? currentMarkPnL = null, double riskFreeRate = 0.04)
        {
            decimal width = Math.Abs(shortStrike - longStrike);
            decimal maxProfit = netCredit * contracts * 100m;
            decimal maxLoss = -(width - netCredit) * contracts * 100m;
            decimal breakeven = right == OptionRight.Put ? shortStrike - netCredit : shortStrike + netCredit;

            var prices = BuildPriceRange(underlyingPrice, shortStrike, longStrike);

            var atExpiration = prices.Select(p =>
            {
                double s = (double)p;
                double intrinsicShort = BlackScholesCalculator.IntrinsicValue(right, s, (double)shortStrike);
                double intrinsicLong = BlackScholesCalculator.IntrinsicValue(right, s, (double)longStrike);
                decimal pnl = netCredit * contracts * 100m + (decimal)(intrinsicLong - intrinsicShort) * contracts * 100m;
                return new PayoffPoint { UnderlyingPrice = p, PnL = pnl };
            }).ToList();

            var today = prices.Select(p =>
            {
                double s = (double)p;
                double priceShort = BlackScholesCalculator.TheoreticalPrice(right, s, (double)shortStrike, yearsToExpiry, riskFreeRate, impliedVolatility);
                double priceLong = BlackScholesCalculator.TheoreticalPrice(right, s, (double)longStrike, yearsToExpiry, riskFreeRate, impliedVolatility);
                decimal pnl = netCredit * contracts * 100m + (decimal)(priceLong - priceShort) * contracts * 100m;
                return new PayoffPoint { UnderlyingPrice = p, PnL = pnl };
            }).ToList();

            return new VerticalSpreadPayoff
            {
                AtExpiration = atExpiration,
                Today = today,
                MaxProfit = maxProfit,
                MaxLoss = maxLoss,
                BreakevenPrice = breakeven,
                CurrentUnderlyingPrice = underlyingPrice,
                CurrentMarkPnL = currentMarkPnL
            };
        }

        public (decimal spreadMarkPrice, decimal unrealizedPnL, decimal? netDelta) ComputeMark(
            OptionContractDto shortLeg, OptionContractDto longLeg, decimal netCreditReceived, int contracts)
        {
            decimal spreadMarkPrice = shortLeg.Mid - longLeg.Mid; // current cost to close (buy back short, sell long)
            decimal unrealizedPnL = (netCreditReceived - spreadMarkPrice) * contracts * 100m;
            decimal? netDelta = (longLeg.Delta.HasValue || shortLeg.Delta.HasValue)
                ? (longLeg.Delta ?? 0m) - (shortLeg.Delta ?? 0m)
                : null;
            return (spreadMarkPrice, unrealizedPnL, netDelta);
        }

        /// <summary>Evenly-spaced candidate underlying prices, centered on the current spot but
        /// widened if needed so both strikes (plus a little headroom) are always inside the
        /// range — a spread built far OTM shouldn't produce a chart that never reaches its own
        /// breakeven.</summary>
        private static List<decimal> BuildPriceRange(decimal underlyingPrice, decimal shortStrike, decimal longStrike)
        {
            decimal center = underlyingPrice > 0 ? underlyingPrice : (shortStrike + longStrike) / 2m;
            decimal spanFromCenter = center * (decimal)PriceRangeFraction;
            decimal strikeSpan = Math.Max(Math.Abs(center - shortStrike), Math.Abs(center - longStrike)) * 1.2m;
            decimal halfSpan = Math.Max(spanFromCenter, strikeSpan);
            decimal low = Math.Max(0.01m, center - halfSpan);
            decimal high = center + halfSpan;

            var points = new List<decimal>(PricePoints);
            decimal step = (high - low) / (PricePoints - 1);
            for (int i = 0; i < PricePoints; i++)
                points.Add(Math.Round(low + step * i, 2));
            return points;
        }
    }
}
