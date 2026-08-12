using LeverageTradingStrategies.Infrastructure.Configuration;
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
    ///
    /// Every method that needs a tuning parameter takes a TqqqWeeklyRuntimeConfig instead of
    /// reading it from IOptions at construction time — resolve it fresh (ITqqqWeeklyConfigProvider.
    /// GetAsync) once per tick and pass it in, so a value tuned directly in the StrategyConfig
    /// DB table takes effect on the very next tick with no app restart.
    /// </summary>
    public interface ITqqqWeeklyStrategyService
    {
        /// <summary>Call once, on the first tick after session open on each trading day.
        /// Idempotent per trading date (state.LastSessionOpenDate guards re-entry). Handles
        /// the weekly Monday-open entry (vol-gated sizing) when flat, or the avg-down check +
        /// tiered take-profit target recompute when already holding. deployedCapital is this
        /// strategy INSTANCE's own capital allocation (StrategyInstanceRecord.CurrentCapital),
        /// not raw broker account equity.</summary>
        TqqqWeeklyDecision EvaluateSessionOpen(TqqqWeeklyRuntimeConfig config, TqqqWeeklyState state, DateOnly tradingDate, decimal sessionOpenPrice, decimal deployedCapital);

        /// <summary>Call on every intraday tick while holding. currentHigh should be the
        /// highest price observed so far this session. No-ops on the entry day itself (no
        /// target is set until the following day — the entry-day blind spot, which still
        /// applies to take-profit specifically; the entry-day STOP-LOSS is a separate check,
        /// see EvaluateSessionClose). Takes no config -- the target price it checks against
        /// was already computed (using config) by EvaluateSessionOpen.</summary>
        TqqqWeeklyDecision EvaluateIntradayTakeProfit(TqqqWeeklyState state, decimal currentHigh);

        /// <summary>Call once per tick once the configured force-close hour has passed.
        /// Idempotent per trading date. isDayBeforeLastTradingDayOfWeek should be true only
        /// on the day before the week's last trading day (Thursday in a normal week).
        /// Unconditional exit — winner or loser — when ForceCloseWeekly is enabled.</summary>
        TqqqWeeklyDecision EvaluateForceCloseWeekly(TqqqWeeklyRuntimeConfig config, TqqqWeeklyState state, DateOnly tradingDate, bool isDayBeforeLastTradingDayOfWeek, decimal currentPrice);

        /// <summary>Call once near session close. Idempotent per trading date. Handles the
        /// close-based stop (CloseStopPct on any day after entry, OR EntryDayCloseStopPct on
        /// the entry day itself — the entry-day variant is a NEW addition on top of the
        /// verified backtest, which has no stop check at all on the entry day) and the
        /// end-of-week backstop safety net (should essentially never fire when
        /// force-close-weekly is working).</summary>
        TqqqWeeklyDecision EvaluateSessionClose(TqqqWeeklyRuntimeConfig config, TqqqWeeklyState state, DateOnly tradingDate, bool isLastTradingDayOfWeek, decimal closePrice);

        /// <summary>Call once per trading day, after EvaluateSessionClose, with that day's
        /// closing price. Idempotent per trading date. Rolls the volatility-gate history
        /// forward so tomorrow's EvaluateSessionOpen sizing decision reflects today's data.</summary>
        void RollDailyVolatilityHistory(TqqqWeeklyRuntimeConfig config, TqqqWeeklyState state, DateOnly tradingDate, decimal todaysClose);

        /// <summary>Unconditional square-off for the Kill switch — called directly by the
        /// kill controller endpoint, NOT by the job's normal tick sequence. If flat, this is a
        /// no-op. If holding, sells the full position at currentPrice and clears position
        /// state exactly like every other exit path (same ClearPositionState mutation), so the
        /// resulting order/state are indistinguishable from a normal strategy-driven exit.
        /// Takes no config -- it's an unconditional exit, no tuning parameter applies.
        ///
        /// brokerConfirmedQuantity, when supplied, OVERRIDES state.Quantity as the amount to
        /// sell/reconcile against — the kill switch is a safety-critical path, so the caller
        /// should always pass the broker's own reported position (IBroker.GetSymbolPositionAsync)
        /// rather than trust local state, which can drift. If it's 0 the position is cleared
        /// locally with no sell order placed (broker already shows flat); if it differs from
        /// state.Quantity, the broker figure is used for the sell and the mismatch is called
        /// out in the decision's Reason (RealizedPnL becomes an approximation in that case).</summary>
        TqqqWeeklyDecision EvaluateKillSwitch(TqqqWeeklyState state, decimal currentPrice, int? brokerConfirmedQuantity = null);
    }
}
