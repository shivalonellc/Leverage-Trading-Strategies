namespace LeverageTradingStrategies.Domain.TqqqAgent
{
    /// <summary>Deterministic, no-LLM gate between Claude's decision and a real order -- this is
    /// the entire "Code decides" half of TQQQ_Intraday_Agent_Spec_v1.md's architecture diagram
    /// (see §2/§7). Nothing here calls out to Claude, Tradier, Redis, or SQLite; it's a pure
    /// function of the inputs it's given, which makes it trivial to unit-test against the 9
    /// checks independently of the rest of the pipeline.
    ///
    /// Checks 1 (symbol lock) and 2 (long-only) are enforced by the type system rather than a
    /// runtime branch here: TqqqAgentAction has no short-sell variant, and this whole module
    /// only ever fetches/considers TQQQ, so there's no "wrong symbol" code path to reject.</summary>
    public class TqqqAgentValidatorService : ITqqqAgentValidator
    {
        public TqqqAgentValidationResult Validate(
            TqqqAgentRawDecision decision,
            TqqqAgentPortfolioSnapshot portfolio,
            DateTime nowEastern,
            bool killSwitchActive,
            int proposedBuyShares,
            TqqqAgentValidatorConfig config)
        {
            // Check 9: kill switch -- independent of and checked before everything else. (The
            // job also gates on this before even calling Claude, saving an API call; this is the
            // defense-in-depth copy of that same check.)
            if (killSwitchActive)
                return Reject(9, "Kill switch active -- no orders placed this cycle.");

            var timeOfDay = nowEastern.TimeOfDay;
            var marketOpen = new TimeSpan(config.MarketOpenHourEt, config.MarketOpenMinuteEt, 0);
            var marketClose = new TimeSpan(config.MarketCloseHourEt, config.MarketCloseMinuteEt, 0);
            var entryStart = new TimeSpan(config.EntryWindowStartHourEt, config.EntryWindowStartMinuteEt, 0);
            var entryEnd = new TimeSpan(config.EntryWindowEndHourEt, config.EntryWindowEndMinuteEt, 0);
            var flattenCutover = new TimeSpan(config.ForceFlattenHourEt, config.ForceFlattenMinuteEt, 0);

            // Check 8 (outer clause): nothing at all outside 9:30-16:00 ET.
            if (timeOfDay < marketOpen || timeOfDay >= marketClose)
                return Reject(8, $"Outside market hours ({timeOfDay:hh\\:mm} ET) -- no action taken.");

            // Check 4: forced flatten. Takes priority over whatever Claude decided this cycle --
            // if we're past the cutover and still holding, sell, full stop.
            if (timeOfDay >= flattenCutover && portfolio.Holding)
            {
                return new TqqqAgentValidationResult
                {
                    Approved = true,
                    FinalAction = TqqqAgentAction.Sell,
                    Shares = portfolio.Quantity,
                    RejectReason = null,
                    FailedCheckNumber = 0
                };
            }

            if (decision.Action == TqqqAgentAction.Hold)
            {
                return new TqqqAgentValidationResult { Approved = true, FinalAction = TqqqAgentAction.Hold, Shares = 0 };
            }

            if (decision.Action == TqqqAgentAction.Sell)
            {
                // Nothing to close -- treat as a no-op Hold rather than a hard rejection; this
                // isn't a risk violation, just Claude asking to exit a position that isn't open.
                if (!portfolio.Holding)
                    return new TqqqAgentValidationResult { Approved = true, FinalAction = TqqqAgentAction.Hold, Shares = 0, RejectReason = "Sell requested with no open position -- treated as Hold." };

                return new TqqqAgentValidationResult { Approved = true, FinalAction = TqqqAgentAction.Sell, Shares = portfolio.Quantity };
            }

            // decision.Action == Buy from here on.

            // Check 3: one position at a time, no pyramiding.
            if (portfolio.Holding)
                return Reject(3, "Buy requested but a position is already open -- no adding to an existing position.");

            // Check 8 (inner clause): new entries only inside the entry window.
            if (timeOfDay < entryStart || timeOfDay >= entryEnd)
                return Reject(8, $"Outside the new-entry window ({entryStart:hh\\:mm}-{entryEnd:hh\\:mm} ET) -- no new entries this late/early.");

            // Optional confidence floor (§6) -- disabled by default (MinConfidenceToAct = 0).
            // Not one of the numbered 9 checks; it's a separate, explicitly-opt-in risk knob.
            if (config.MinConfidenceToAct > 0 && decision.Confidence < config.MinConfidenceToAct)
                return Reject(0, $"Confidence {decision.Confidence:0.00} below the configured floor {config.MinConfidenceToAct:0.00} -- Buy not acted on.");

            // Check 5: 3-consecutive-losses circuit breaker.
            if (portfolio.ConsecutiveLossesToday >= config.ConsecutiveLossLimit)
                return Reject(5, $"{portfolio.ConsecutiveLossesToday} consecutive losses today -- new entries halted for the rest of the day.");

            // Check 6: daily loss stop, measured against START-of-day equity (not live equity,
            // which already reflects today's P&L and would make the percentage circular).
            var dailyLossThreshold = -Math.Abs(config.DailyLossStopPct) * portfolio.DayStartEquity;
            if (portfolio.RealizedPnLToday <= dailyLossThreshold)
                return Reject(6, $"Realized P&L today ({portfolio.RealizedPnLToday:C}) has hit the daily loss stop ({dailyLossThreshold:C}) -- new entries halted for the rest of the day.");

            // Check 7: sizing ceiling rounded to zero -- not a rule violation, just nothing
            // tradable at the current price/cash level. Skip rather than reject-with-blame.
            if (proposedBuyShares <= 0)
                return new TqqqAgentValidationResult { Approved = false, FinalAction = TqqqAgentAction.Hold, Shares = 0, RejectReason = "Computed position size rounded to 0 shares -- trade skipped.", FailedCheckNumber = 7 };

            return new TqqqAgentValidationResult { Approved = true, FinalAction = TqqqAgentAction.Buy, Shares = proposedBuyShares };
        }

        private static TqqqAgentValidationResult Reject(int checkNumber, string reason) => new()
        {
            Approved = false,
            FinalAction = TqqqAgentAction.Hold,
            Shares = 0,
            RejectReason = reason,
            FailedCheckNumber = checkNumber
        };
    }
}
