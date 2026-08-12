// File: LeverageTradingStrategies.Infrastructure/Options/BlackScholesCalculator.cs
// Ported verbatim from MarketMatrixPreparer.Infrastructure.Options.BlackScholesCalculator
// (see that repo for the original design notes on why this is hand-rolled instead of relying
// on System.Math.Erf). Used here NOT to pick strikes (the vertical-spread builder reads real
// strikes/deltas/bid-ask straight off Tradier's chain -- no theoretical strike-solve needed for
// that), but to reprice both legs across a RANGE of hypothetical underlying prices for "today"
// (current DTE, current implied vol) -- Tradier's chain only gives you the option's greeks/price
// at the CURRENT spot, not a curve, and the live payoff chart needs a curve.
namespace LeverageTradingStrategies.Infrastructure.Options
{
    public enum OptionRight
    {
        Call,
        Put
    }

    /// <summary>Pure Black-Scholes math -- no API/DB dependency, no state. European-style
    /// pricing (no early-exercise adjustment); a reasonable approximation for the equity/ETF
    /// options this module targets over the short DTEs a weekly/monthly credit spread uses.</summary>
    public static class BlackScholesCalculator
    {
        private const double MinYearsToExpiry = 1.0 / 365.0 / 24.0; // 1 hour floor — avoids div-by-zero/√0 as expiry approaches
        private const double MinVolatility = 0.0001;
        private const double MaxVolatility = 5.0; // 500% — generous upper bound for a solver ceiling, not a realistic estimate

        /// <summary>Standard normal cumulative distribution function via the Abramowitz &amp;
        /// Stegun 7.1.26 rational approximation (max absolute error ~7.5e-8).</summary>
        public static double NormalCdf(double x)
        {
            const double a1 = 0.254829592;
            const double a2 = -0.284496736;
            const double a3 = 1.421413741;
            const double a4 = -1.453152027;
            const double a5 = 1.061405429;
            const double p = 0.3275911;

            int sign = x < 0 ? -1 : 1;
            double absX = Math.Abs(x) / Math.Sqrt(2.0);

            double t = 1.0 / (1.0 + p * absX);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-absX * absX);

            return 0.5 * (1.0 + sign * y);
        }

        public static double NormalPdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);

        private static double D1(double underlyingPrice, double strike, double yearsToExpiry, double riskFreeRate, double volatility)
        {
            double t = Math.Max(yearsToExpiry, MinYearsToExpiry);
            double v = Math.Max(volatility, MinVolatility);
            return (Math.Log(underlyingPrice / strike) + (riskFreeRate + 0.5 * v * v) * t) / (v * Math.Sqrt(t));
        }

        /// <summary>Theoretical European option price under Black-Scholes for a given
        /// volatility -- this is the core function the live payoff curve leans on: reprice
        /// both legs at a hypothetical underlying price, with today's actual remaining
        /// time-to-expiry and implied vol (not zero/expiration), to show what the spread is
        /// theoretically worth right now across a range of prices.</summary>
        public static double TheoreticalPrice(
            OptionRight right, double underlyingPrice, double strike, double yearsToExpiry,
            double riskFreeRate, double volatility)
        {
            double t = Math.Max(yearsToExpiry, MinYearsToExpiry);
            double v = Math.Max(volatility, MinVolatility);
            double d1 = D1(underlyingPrice, strike, t, riskFreeRate, v);
            double d2 = d1 - v * Math.Sqrt(t);
            double discount = Math.Exp(-riskFreeRate * t);

            return right == OptionRight.Call
                ? underlyingPrice * NormalCdf(d1) - strike * discount * NormalCdf(d2)
                : strike * discount * NormalCdf(-d2) - underlyingPrice * NormalCdf(-d1);
        }

        /// <summary>Intrinsic value only (no time value) -- used for the payoff-AT-EXPIRATION
        /// curve, where time value is by definition zero.</summary>
        public static double IntrinsicValue(OptionRight right, double underlyingPrice, double strike) =>
            right == OptionRight.Call ? Math.Max(0, underlyingPrice - strike) : Math.Max(0, strike - underlyingPrice);

        /// <summary>Standard Black-Scholes delta given an already-known volatility: N(d1) for
        /// a call, N(d1)-1 for a put (signed).</summary>
        public static double Delta(
            OptionRight right, double underlyingPrice, double strike, double yearsToExpiry,
            double riskFreeRate, double volatility)
        {
            double t = Math.Max(yearsToExpiry, MinYearsToExpiry);
            double v = Math.Max(volatility, MinVolatility);
            double d1 = D1(underlyingPrice, strike, t, riskFreeRate, v);
            return right == OptionRight.Call ? NormalCdf(d1) : NormalCdf(d1) - 1.0;
        }
    }
}
