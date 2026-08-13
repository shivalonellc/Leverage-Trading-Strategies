namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    public interface IAnthropicDecisionClient
    {
        /// <summary>Runs one full decision cycle against the Anthropic Messages API: Claude sees
        /// the already-gathered portfolio/market/recent-decisions context via tool calls (no live
        /// I/O happens inside this loop -- the data is fixed for the whole cycle) and reasons
        /// freely (spec §6 -- no hardcoded strategy rules in the prompt) until it calls
        /// submit_decision. Never throws -- any API/parsing failure or an exhausted tool-loop
        /// safety cap returns a Hold decision rather than propagating, since "no trade" is always
        /// the safe default for this cycle.</summary>
        Task<TqqqAgentRawDecision> GetDecisionAsync(
            TqqqAgentPortfolioSnapshot portfolio,
            TqqqAgentMarketSnapshot market,
            List<TqqqAgentRecentDecision> recentDecisions,
            CancellationToken ct = default);
    }
}
