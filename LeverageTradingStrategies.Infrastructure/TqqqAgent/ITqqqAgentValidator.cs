namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public interface ITqqqAgentValidator
    {
        /// <summary>Runs the 9 hard-limit checks from TQQQ_Intraday_Agent_Spec_v1.md §7 against
        /// Claude's raw decision. Pure function, no I/O -- the caller (TqqqAgentJob) is
        /// responsible for gathering portfolio/time state and the pre-computed proposedBuyShares
        /// (from ITqqqAgentSizingService) before calling this.</summary>
        TqqqAgentValidationResult Validate(
            TqqqAgentRawDecision decision,
            TqqqAgentPortfolioSnapshot portfolio,
            DateTime nowEastern,
            bool killSwitchActive,
            int proposedBuyShares,
            TqqqAgentValidatorConfig config);
    }
}
