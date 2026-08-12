using LeverageTradingStrategies.Infrastructure.Models;

namespace LeverageTradingStrategies.Domain.Tqqq
{
    /// <summary>
    /// Pure decision logic for the verified TQQQ weekly strategy baseline (49.9% CAGR /
    /// 22.1% max DD backtest). Every method mutates the passed-in state in place (an
    /// intentional "best-case fill" optimistic-execution model matching the backtest's own
    /// assumptions — see TQQQ_Weekly_Strategy_Spec_v1.md Section 8) and returns the action
    /// (if any) the caller should execute via IBroker. The caller (TqqqWeeklyLiveTradingJob)
    /// is responsible for calling each method at the right point in the trading day and for
    /// persisting state afterward.
    /// </summary>
    public interface ITqqqWeeklyStrategyService
    {
        /// <summary>Call once, on the first tick after session open on each trading day.
        /// Idempotent per trading date (state.LastSessionOpenDate guards re-entry). Handles
        /// the weekly Monday-open entry (vol-gated sizing) when flat, or the avg-down check +
        /// tiered take-profit target recompute when already holding.</summary>
        TqqqWeeklyDecision EvaluateSessionOpen(TqqqWeeklyState state, DateOnly tradingDate, decimal sessionOpenPrice, decimal portfolioValue);

        /// <summary>Call on every intraday tick while holding. currentHigh should be the
        /// highest price observed so far this session. No-ops on the entry day itself (no
        /// target is set until the following day — the entry-day blind spot).</summary>
        TqqqWeeklyDecision EvaluateIntradayTakeProfit(TqqqWeeklyState state, decimal currentHigh);

        /// <summary>Call once per tick once the configured force-close hour has passed.
        /// Idempotent per trading date. isDayBeforeLastTradingDayOfWeek should be true only
        /// on the day before the week's last trading day (Thursday in a normal week).
        /// Unconditional exit — winner or loser — when ForceCloseWeekly is enabled.</summary>
        TqqqWeeklyDecision EvaluateForceCloseWeekly(TqqqWeeklyState state, DateOnly tradingDate, bool isDayBeforeLastTradingDayOfWeek, decimal currentPrice);

        /// <summary>Call once near session close. Idempotent per trading date. Handles the
        /// close-based -20% stop (skipped on the entry day) and the end-of-week backstop
        /// safety net (should essentially never fire when force-close-weekly is working).</summary>
        TqqqWeeklyDecision EvaluateSessionClose(TqqqWeeklyState state, DateOnly tradingDate, bool isLastTradingDayOfWeek, decimal closePrice);

        /// <summary>Call once per trading day, after EvaluateSessionClose, with that day's
        /// closing price. Idempotent per trading date. Rolls the volatility-gate history
        /// forward so tomorrow's EvaluateSessionOpen sizing decision reflects today's data.</summary>
        void RollDailyVolatilityHistory(TqqqWeeklyState state, DateOnly tradingDate, decimal todaysClose);
    }
}
