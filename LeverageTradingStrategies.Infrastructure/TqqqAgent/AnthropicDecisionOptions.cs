namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Plain DTO the job maps AppSettingsOptions.TqqqAgent onto per-call (same
    /// decoupling reason as TqqqAgentValidatorConfig -- keeps AnthropicDecisionClient free of a
    /// hard dependency on Infrastructure.Configuration's shape). The API key itself is NOT here
    /// -- it's set once as a default header on the HttpClient at DI-registration time (see
    /// Program.cs), never passed around per-call.</summary>
    public class AnthropicDecisionOptions
    {
        /// <summary>Anthropic model string, e.g. "claude-sonnet-5". Deliberately configurable
        /// rather than hardcoded -- Claude model names change over time and this is a
        /// long-running unattended job.</summary>
        public string Model { get; set; } = "claude-sonnet-5";

        public int MaxTokens { get; set; } = 1024;

        /// <summary>Safety cap on the tool-use loop -- if Claude hasn't called submit_decision
        /// within this many turns, the cycle defaults to Hold rather than looping indefinitely
        /// or burning an unbounded number of API calls.</summary>
        public int MaxToolIterations { get; set; } = 6;
    }
}
