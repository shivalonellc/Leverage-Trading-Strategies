using LeverageTradingStrategies.Infrastructure.Configuration;
using LeverageTradingStrategies.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeverageTradingStrategies.Domain.Tqqq
{
    /// <summary>
    /// C# port of the verified TQQQ weekly strategy baseline (fleury_tqqq_weekly_replication.py
    /// + the force-close-weekly and Monday-avg-down-bounce additions). Field/parameter names
    /// and comments below reference the corresponding section of TQQQ_Weekly_Strategy_Spec_v1.md.
    ///
    /// IMPORTANT — one known open item versus the Python backtest: the Python replication
    /// faithfully preserves a source quirk where ANY buy fill (including an avg-down add)
    /// resets entry_price to that fill's price rather than blending it. This class implements
    /// that same reset (see EvaluateHoldingOnSessionOpen) based on the prior analysis summary,
    /// but the exact same-day ordering versus the tier-target recompute has not been
    /// re-verified line-by-line against the live Python source as part of this port — verify
    /// against fleury_tqqq_weekly_replication.py before relying on this in live trading with
    /// real capital.
    /// </summary>
    public class TqqqWeeklyStrategyService : ITqqqWeeklyStrategyService
    {
        private readonly TqqqWeeklyOptions _options;
        private readonly ILogger<TqqqWeeklyStrategyService> _logger;

        public TqqqWeeklyStrategyService(IOptions<AppSettingsOptions> options, ILogger<TqqqWeeklyStrategyService> logger)
        {
            _options = options.Value.TqqqWeekly;
            _logger = logger;
        }

        public TqqqWeeklyDecision EvaluateSessionOpen(TqqqWeeklyState state, DateOnly tradingDate, decimal sessionOpenPrice, decimal portfolioValue)
        {
            if (state.LastSessionOpenDate == tradingDate)
                return TqqqWeeklyDecision.None("Session-open already evaluated for this trading date");
            state.LastSessionOpenDate = tradingDate;

            int weekKey = IsoWeekKey(tradingDate);
            bool isNewWeek = state.CurrentIsoWeekKey != weekKey;
            if (isNewWeek)
            {
                state.CurrentIsoWeekKey = weekKey;
                state.TradedThisWeek = false;
            }

            return state.Holding
                ? EvaluateHoldingOnSessionOpen(state, sessionOpenPrice, portfolioValue)
                : EvaluateEntryOnSessionOpen(state, tradingDate, sessionOpenPrice, portfolioValue, isNewWeek);
        }

        private TqqqWeeklyDecision EvaluateHoldingOnSessionOpen(TqqqWeeklyState state, decimal sessionOpenPrice, decimal portfolioValue)
        {
            if (state.RecentDailyCloses.Count == 0)
            {
                // No prior close known yet -- shouldn't normally happen once a position is
                // open, since RollDailyVolatilityHistory runs every trading day including the
                // entry day. Clear the target defensively rather than checking against a stale
                // or zero value.
                state.CurrentTargetPrice = 0m;
                return TqqqWeeklyDecision.None("Holding, but no prior close available yet for profit calc");
            }

            decimal yesterdayClose = state.RecentDailyCloses[^1];
            decimal entryPriceAtStartOfDay = state.EntryPrice;
            // step 5a (spec order-of-operations): profit computed off the ORIGINAL entry price
            decimal profit = entryPriceAtStartOfDay != 0
                ? (yesterdayClose - entryPriceAtStartOfDay) / entryPriceAtStartOfDay
                : 0m;

            decimal effectiveTrigger = _options.AvgDownTrigger;
            decimal effectiveFraction = _options.AvgDownFraction;
            bool isMondaySpecialWindow = state.EnteredOnMonday && !state.MondayAvgDownWindowConsumed;
            if (isMondaySpecialWindow)
            {
                effectiveTrigger = _options.MondayAvgDownTrigger;
                effectiveFraction = _options.MondayAvgDownFraction;
            }

            var decision = TqqqWeeklyDecision.None("Holding — no avg-down trigger hit");

            // step 5b: avg-down check (may reset state.EntryPrice to today's fill price)
            if (!state.AddedThisPosition && profit <= effectiveTrigger)
            {
                int addQty = (int)Math.Floor(portfolioValue * effectiveFraction / yesterdayClose);
                if (addQty > 0)
                {
                    state.AddedThisPosition = true;
                    state.Quantity += addQty;
                    state.EntryPrice = sessionOpenPrice; // faithful replication quirk — see class remarks
                    decision = TqqqWeeklyDecision.AddToPosition(addQty,
                        $"Avg-down trigger hit: profit {profit:P2} <= {effectiveTrigger:P2} " +
                        $"({(isMondaySpecialWindow ? "Monday-special -3%/20%" : "standard -5%/30%")}); " +
                        $"{addQty} sh sized off prior close {yesterdayClose:C}, filling ~{sessionOpenPrice:C}");
                }
                else
                {
                    _logger.LogWarning("Avg-down trigger hit (profit {Profit:P2}) but computed add-quantity rounded to 0 shares", profit);
                }
            }

            if (isMondaySpecialWindow)
                state.MondayAvgDownWindowConsumed = true; // window is exactly one day, consumed regardless of firing

            // step 5c: tiered take-profit target, off the (possibly just-reset) entry price
            decimal tierMultiplier =
                profit > _options.TierProfitHighThreshold ? _options.TierHighMultiplier :
                profit > 0m ? _options.TierMidMultiplier :
                _options.TierLowMultiplier;
            state.CurrentTargetPrice = state.EntryPrice * tierMultiplier;

            return decision;
        }

        private TqqqWeeklyDecision EvaluateEntryOnSessionOpen(TqqqWeeklyState state, DateOnly tradingDate, decimal sessionOpenPrice, decimal portfolioValue, bool isNewWeek)
        {
            // One-time deploy guard: if this strategy instance has never run before, only
            // auto-start on an actual Monday so the very first cycle isn't a truncated,
            // off-schedule mid-week entry (and, as a side effect, guarantees we never open an
            // unmanaged position right before a weekend on day one).
            if (!state.HasEverRun)
            {
                state.HasEverRun = true;
                state.DeployGuardConsumed = true;
                if (tradingDate.DayOfWeek != DayOfWeek.Monday)
                {
                    return TqqqWeeklyDecision.None(
                        "Deploy guard: first-ever run did not land on a Monday — waiting for next Monday to start the weekly cycle cleanly");
                }
            }

            bool isFirstTradingDayOfWeek = isNewWeek;
            if (!isFirstTradingDayOfWeek || state.TradedThisWeek)
                return TqqqWeeklyDecision.None("Flat, but not the weekly entry day (or already traded this week)");

            decimal frac = state.VolGateClosedToday ? _options.VolBoostFraction : _options.BaseSizeFraction;
            int qty = (int)Math.Floor(portfolioValue * frac / sessionOpenPrice);
            if (qty <= 0)
            {
                _logger.LogWarning("Weekly entry day but computed size rounded to 0 shares (portfolioValue={PortfolioValue}, price={Price})", portfolioValue, sessionOpenPrice);
                return TqqqWeeklyDecision.None("Flat, weekly entry day, but computed size rounds to 0 shares");
            }

            state.Holding = true;
            state.EntryPrice = sessionOpenPrice;
            state.Quantity = qty;
            state.EntryDate = tradingDate;
            state.EnteredOnMonday = tradingDate.DayOfWeek == DayOfWeek.Monday;
            state.AddedThisPosition = false;
            state.MondayAvgDownWindowConsumed = false;
            state.TradedThisWeek = true;
            state.CurrentTargetPrice = 0m; // entry-day blind spot: no take-profit target until tomorrow

            return TqqqWeeklyDecision.EnterLong(qty,
                $"Weekly entry: {qty} sh @ ~{sessionOpenPrice:C} ({(state.VolGateClosedToday ? "vol-boost 1.25x" : "base 0.98x")} sizing)");
        }

        public TqqqWeeklyDecision EvaluateIntradayTakeProfit(TqqqWeeklyState state, decimal currentHigh)
        {
            if (!state.Holding)
                return TqqqWeeklyDecision.None("Flat — no take-profit to check");

            if (state.CurrentTargetPrice <= 0)
                return TqqqWeeklyDecision.None("No take-profit target set yet (entry-day blind spot, or not yet computed)");

            if (currentHigh >= state.CurrentTargetPrice)
            {
                var decision = TqqqWeeklyDecision.SellAll(state.Quantity,
                    $"Take-profit touched: high {currentHigh:C} >= target {state.CurrentTargetPrice:C} (entry {state.EntryPrice:C})");
                ClearPositionState(state);
                return decision;
            }

            return TqqqWeeklyDecision.None("Take-profit target not yet touched");
        }

        public TqqqWeeklyDecision EvaluateForceCloseWeekly(TqqqWeeklyState state, DateOnly tradingDate, bool isDayBeforeLastTradingDayOfWeek, decimal currentPrice)
        {
            if (!_options.ForceCloseWeekly)
                return TqqqWeeklyDecision.None("Force-close-weekly disabled in config");

            if (!isDayBeforeLastTradingDayOfWeek)
                return TqqqWeeklyDecision.None("Not the force-close day");

            if (state.LastForceCloseCheckDate == tradingDate)
                return TqqqWeeklyDecision.None("Force-close already evaluated for this trading date");
            state.LastForceCloseCheckDate = tradingDate;

            if (!state.Holding)
                return TqqqWeeklyDecision.None("Force-close day, but flat");

            var decision = TqqqWeeklyDecision.SellAll(state.Quantity,
                $"Force-close-weekly: unconditional exit on day-before-last-trading-day (entry {state.EntryPrice:C}, current {currentPrice:C})");
            ClearPositionState(state);
            return decision;
        }

        public TqqqWeeklyDecision EvaluateSessionClose(TqqqWeeklyState state, DateOnly tradingDate, bool isLastTradingDayOfWeek, decimal closePrice)
        {
            if (state.LastSessionCloseDate == tradingDate)
                return TqqqWeeklyDecision.None("Session-close already evaluated for this trading date");
            state.LastSessionCloseDate = tradingDate;

            if (!state.Holding)
                return TqqqWeeklyDecision.None("Flat at session close");

            bool isEntryDay = state.EntryDate == tradingDate;

            // Close-based -20% stop — skipped on the entry day (entry-day blind spot, Section 3)
            if (!isEntryDay && state.EntryPrice != 0)
            {
                decimal closeProfit = (closePrice - state.EntryPrice) / state.EntryPrice;
                if (closeProfit <= _options.CloseStopPct)
                {
                    var stopDecision = TqqqWeeklyDecision.SellAll(state.Quantity,
                        $"Close-based stop: close {closePrice:C} is {closeProfit:P2} vs entry {state.EntryPrice:C} (<= {_options.CloseStopPct:P0})");
                    ClearPositionState(state);
                    return stopDecision;
                }
            }

            // EOW backstop safety net — should essentially never fire when force-close-weekly
            // is enabled and working correctly. If it does fire, that's worth investigating.
            if (isLastTradingDayOfWeek)
            {
                _logger.LogWarning("EOW backstop fired — still holding {Symbol} on the last trading day of the week. " +
                    "Force-close-weekly should have already closed this on the prior day; investigate.", state.Symbol);
                var backstop = TqqqWeeklyDecision.SellAll(state.Quantity,
                    "EOW backstop: still holding on the last trading day of the week (unexpected — force-close should have already fired)");
                ClearPositionState(state);
                return backstop;
            }

            return TqqqWeeklyDecision.None("Session-close checks passed, still holding");
        }

        public void RollDailyVolatilityHistory(TqqqWeeklyState state, DateOnly tradingDate, decimal todaysClose)
        {
            if (state.LastVolRollDate == tradingDate)
                return;
            state.LastVolRollDate = tradingDate;

            state.RecentDailyCloses.Add(todaysClose);
            int maxCloses = _options.VolLookbackDays + 1;
            while (state.RecentDailyCloses.Count > maxCloses)
                state.RecentDailyCloses.RemoveAt(0);

            if (state.RecentDailyCloses.Count < _options.VolLookbackDays + 1)
            {
                state.VolGateClosedToday = false;
                return;
            }

            var rets = new List<double>(_options.VolLookbackDays);
            for (int i = 1; i < state.RecentDailyCloses.Count; i++)
            {
                decimal prev = state.RecentDailyCloses[i - 1];
                decimal cur = state.RecentDailyCloses[i];
                if (prev != 0)
                    rets.Add((double)((cur - prev) / prev));
            }

            double v = PopulationStdDev(rets);
            state.VolHistory.Add(v);
            while (state.VolHistory.Count > _options.VolHistoryMaxReadings)
                state.VolHistory.RemoveAt(0);

            if (state.VolHistory.Count >= _options.VolHistoryMinReadings)
            {
                var sorted = state.VolHistory.OrderBy(x => x).ToList();
                int idx = (int)(_options.VolPercentileThreshold * (sorted.Count - 1));
                double threshold = sorted[idx];
                state.VolGateClosedToday = v >= threshold;
            }
            else
            {
                state.VolGateClosedToday = false;
            }
        }

        private static void ClearPositionState(TqqqWeeklyState state)
        {
            state.Holding = false;
            state.EntryPrice = 0m;
            state.Quantity = 0;
            state.EntryDate = null;
            state.EnteredOnMonday = false;
            state.AddedThisPosition = false;
            state.MondayAvgDownWindowConsumed = false;
            state.CurrentTargetPrice = 0m;
        }

        private static double PopulationStdDev(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            double mean = values.Average();
            double sumSq = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sumSq / values.Count);
        }

        private static int IsoWeekKey(DateOnly date)
        {
            var dt = date.ToDateTime(TimeOnly.MinValue);
            int week = System.Globalization.ISOWeek.GetWeekOfYear(dt);
            int year = System.Globalization.ISOWeek.GetYear(dt);
            return (year * 100) + week;
        }
    }
}
