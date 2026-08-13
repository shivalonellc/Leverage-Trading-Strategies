using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.TqqqAgent
{
    /// <summary>Direct HTTP client for the Anthropic Messages API (api.anthropic.com/v1/messages)
    /// -- this is the "Claude decides" box in TQQQ_Intraday_Agent_Spec_v1.md's architecture
    /// diagram, and deliberately a SEPARATE Claude instance/context from whatever chat session
    /// built this code. It has to be: a 5-minute unattended cadence needs to run with no chat
    /// present, and (equally important) the chat assistant that wrote this code is never
    /// permitted to place an order itself -- 100% of order execution happens later, in
    /// deterministic C# (TqqqAgentValidatorService + the job's order-execution step), never
    /// through anything this class's Claude instance invokes directly.
    ///
    /// Built on raw HttpClient + System.Text.Json rather than an SDK, for the same reason
    /// TradierMarketDataRestClient bypasses the Tradier NuGet wrapper: the Messages API's wire
    /// format is small, stable, and fully documented (https://docs.claude.com/en/api/messages),
    /// so hand-rolling it here is lower-risk than adding an unverified dependency in code that
    /// can't be compiled/tested in this environment.
    ///
    /// The HttpClient passed in is expected to already have BaseAddress
    /// ("https://api.anthropic.com") and the "x-api-key" / "anthropic-version" default headers
    /// set -- see Program.cs wiring.</summary>
    public class AnthropicDecisionClient : IAnthropicDecisionClient
    {
        private const string ApiPath = "/v1/messages";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default);

        private readonly HttpClient _http;
        private readonly AnthropicDecisionOptions _options;
        private readonly ILogger<AnthropicDecisionClient> _logger;

        public AnthropicDecisionClient(HttpClient http, AnthropicDecisionOptions options, ILogger<AnthropicDecisionClient> logger)
        {
            _http = http;
            _options = options;
            _logger = logger;
        }

        public async Task<TqqqAgentRawDecision> GetDecisionAsync(
            TqqqAgentPortfolioSnapshot portfolio,
            TqqqAgentMarketSnapshot market,
            List<TqqqAgentRecentDecision> recentDecisions,
            CancellationToken ct = default)
        {
            try
            {
                return await RunToolLoopAsync(portfolio, market, recentDecisions, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnthropicDecisionClient: decision cycle failed -- defaulting to Hold.");
                return new TqqqAgentRawDecision
                {
                    Action = TqqqAgentAction.Hold,
                    Confidence = 0,
                    Why = $"Decision call failed ({ex.GetType().Name}: {ex.Message}) -- defaulting to Hold."
                };
            }
        }

        private async Task<TqqqAgentRawDecision> RunToolLoopAsync(
            TqqqAgentPortfolioSnapshot portfolio,
            TqqqAgentMarketSnapshot market,
            List<TqqqAgentRecentDecision> recentDecisions,
            CancellationToken ct)
        {
            var portfolioJson = JsonSerializer.Serialize(portfolio, JsonOptions);
            var marketJson = JsonSerializer.Serialize(market, JsonOptions);

            // Mutable running transcript sent on every turn -- plain object graphs (not
            // System.Text.Json.Nodes.JsonNode) specifically because JsonNode instances can only
            // ever be attached to one parent at a time, which makes incrementally growing a
            // message history across loop iterations awkward. Anonymous/dictionary objects have
            // no such restriction and JsonSerializer handles them (and embedded JsonElements)
            // natively.
            var messages = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "A new TQQQ decision cycle has started. Use the tools available to " +
                                   "look at the current portfolio, the market snapshot, and your recent " +
                                   "decisions, then call submit_decision exactly once with your decision " +
                                   "for this cycle."
                }
            };

            for (var iteration = 0; iteration < _options.MaxToolIterations; iteration++)
            {
                var requestBody = new Dictionary<string, object?>
                {
                    ["model"] = _options.Model,
                    ["max_tokens"] = _options.MaxTokens,
                    ["system"] = BuildSystemPrompt(),
                    ["messages"] = messages,
                    ["tools"] = BuildToolDefinitions()
                };

                using var content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(ApiPath, content, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("AnthropicDecisionClient: API call failed with {Status}: {Body}", response.StatusCode, responseBody);
                    return HoldFallback($"Anthropic API returned {(int)response.StatusCode} -- defaulting to Hold.");
                }

                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("content", out var contentBlocks) || contentBlocks.ValueKind != JsonValueKind.Array)
                {
                    return HoldFallback("Anthropic API response had no content blocks -- defaulting to Hold.");
                }

                // Echo the assistant's raw content array back verbatim on the next turn --
                // JsonElement serializes natively via System.Text.Json, so no re-modeling needed.
                messages.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = contentBlocks.Clone() });

                var toolResults = new List<object>();
                foreach (var block in contentBlocks.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "tool_use")
                        continue;

                    var toolName = block.GetProperty("name").GetString() ?? string.Empty;
                    var toolUseId = block.GetProperty("id").GetString() ?? string.Empty;
                    var input = block.TryGetProperty("input", out var inputEl) ? inputEl : default;

                    if (toolName == "submit_decision")
                    {
                        var decision = ParseSubmitDecision(input);
                        if (decision != null)
                            return decision;

                        // Malformed submit_decision call -- tell Claude and give it another turn
                        // rather than silently defaulting, since this is very likely recoverable.
                        toolResults.Add(BuildToolResult(toolUseId, "Invalid submit_decision input -- action must be one of Hold/Buy/Sell, confidence a number 0-1, and why a non-empty string. Please call submit_decision again with valid input.", isError: true));
                        continue;
                    }

                    var resultJson = toolName switch
                    {
                        "get_portfolio" => portfolioJson,
                        "get_market_snapshot" => marketJson,
                        "get_recent_decisions" => JsonSerializer.Serialize(FilterRecentDecisions(recentDecisions, input), JsonOptions),
                        _ => null
                    };

                    toolResults.Add(resultJson != null
                        ? BuildToolResult(toolUseId, resultJson, isError: false)
                        : BuildToolResult(toolUseId, $"Unknown tool '{toolName}'.", isError: true));
                }

                if (toolResults.Count == 0)
                {
                    // Claude responded with text only (no tool call) and didn't submit a
                    // decision -- nudge it rather than ending the cycle silently.
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = "You haven't called submit_decision yet. Please call it now with your decision (Hold, Buy, or Sell) for this cycle."
                    });
                    continue;
                }

                messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = toolResults });
            }

            return HoldFallback($"No submit_decision call within {_options.MaxToolIterations} tool-use turns -- defaulting to Hold.");
        }

        private static List<TqqqAgentRecentDecision> FilterRecentDecisions(List<TqqqAgentRecentDecision> all, JsonElement input)
        {
            var limit = 5;
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var parsed) && parsed > 0)
                limit = parsed;
            return all.Take(limit).ToList();
        }

        private static TqqqAgentRawDecision? ParseSubmitDecision(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object)
                return null;

            if (!input.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
                return null;
            if (!Enum.TryParse<TqqqAgentAction>(actionEl.GetString(), ignoreCase: true, out var action))
                return null;

            double confidence = 0;
            if (input.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number)
                confidence = confEl.GetDouble();
            confidence = Math.Clamp(confidence, 0.0, 1.0);

            var why = input.TryGetProperty("why", out var whyEl) && whyEl.ValueKind == JsonValueKind.String
                ? whyEl.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(why))
                return null;

            return new TqqqAgentRawDecision { Action = action, Confidence = confidence, Why = why };
        }

        private static Dictionary<string, object?> BuildToolResult(string toolUseId, string content, bool isError) => new()
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolUseId,
            ["content"] = content,
            ["is_error"] = isError
        };

        private static TqqqAgentRawDecision HoldFallback(string why) => new()
        {
            Action = TqqqAgentAction.Hold,
            Confidence = 0,
            Why = why
        };

        private static string BuildSystemPrompt() => """
            You are the discretionary decision-maker for an autonomous intraday trading system that
            trades exactly one instrument: TQQQ (a 3x leveraged ETF tracking the Nasdaq-100). Your
            job each cycle is to decide Hold, Buy, or Sell for TQQQ, based entirely on your own
            judgment of the context you gather via the available tools.

            Account mechanics (facts about the system, not trading rules): the account can only ever
            be long TQQQ or flat -- short selling is never available. Positions are never held
            overnight; if you are holding TQQQ when the trading day is ending, the system will
            automatically flatten the position regardless of what you decide that cycle, so you do
            not need to manage that yourself. Only one position is open at a time -- if you are
            already holding, a Buy decision will not add to the position.

            You are not given a fixed strategy or a set of technical-analysis rules to follow. Use
            whatever combination of the portfolio state, market indicators, and your own recent
            decision history you find useful, and reason about it however you think is sound. Your
            decision is advisory to a downstream automated risk check -- a separate, non-negotiable
            system enforces position sizing and hard risk limits after you decide, so you do not need
            to compute position size or worry about catastrophic risk controls; focus purely on
            whether Hold, Buy, or Sell is the right call for TQQQ right now.

            Gather whatever context you need using get_portfolio, get_market_snapshot, and
            get_recent_decisions (in any order, calling more than once if you want), then call
            submit_decision exactly once to finish the cycle. submit_decision takes:
              - action: "Hold", "Buy", or "Sell"
              - confidence: a number from 0.0 to 1.0 reflecting how confident you are
              - why: a short (one to three sentence) rationale for the decision
            """;

        private static List<object> BuildToolDefinitions() => new()
        {
            new Dictionary<string, object?>
            {
                ["name"] = "get_portfolio",
                ["description"] = "Returns the current TQQQ position (if any), cash available, total equity, and today's realized P&L / consecutive-loss context.",
                ["input_schema"] = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = new Dictionary<string, object?>(), ["required"] = Array.Empty<string>() }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "get_market_snapshot",
                ["description"] = "Returns current TQQQ price/indicator data (VWAP, EMAs, RSI, MACD, ATR, day range, gap, relative volume) plus QQQ regime context.",
                ["input_schema"] = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = new Dictionary<string, object?>(), ["required"] = Array.Empty<string>() }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "get_recent_decisions",
                ["description"] = "Returns your most recent past decisions (action, confidence, why, whether it was executed, and realized P&L if it closed a trade) for continuity across cycles.",
                ["input_schema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["limit"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "How many recent decisions to return. Defaults to 5." }
                    },
                    ["required"] = Array.Empty<string>()
                }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "submit_decision",
                ["description"] = "Finalizes your decision for this cycle. Call exactly once, after gathering whatever context you need.",
                ["input_schema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["action"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "Hold", "Buy", "Sell" } },
                        ["confidence"] = new Dictionary<string, object?> { ["type"] = "number", ["description"] = "0.0 to 1.0" },
                        ["why"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Short rationale for the decision." }
                    },
                    ["required"] = new[] { "action", "confidence", "why" }
                }
            }
        };
    }
}
