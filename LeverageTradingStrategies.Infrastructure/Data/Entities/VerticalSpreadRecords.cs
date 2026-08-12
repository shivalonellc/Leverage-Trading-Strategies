using LeverageTradingStrategies.Infrastructure.Options;

namespace LeverageTradingStrategies.Infrastructure.Data.Entities
{
    /// <summary>Bull put credit spread (both legs puts, sells the higher/closer strike, buys
    /// the lower/further strike as protection) or bear call credit spread (both legs calls,
    /// sells the lower/closer strike, buys the higher/further strike as protection). Both are
    /// "vertical credit spreads" -- same combo-order mechanics, opposite side of the market.</summary>
    public enum VerticalSpreadType
    {
        BullPutCredit,
        BearCallCredit
    }

    /// <summary>Paper: saved and mark-to-market tracked against real Tradier quotes, no broker
    /// order placed anywhere. Live: the real Schwab combo order was confirmed filled. Closed:
    /// the position (paper or live) was closed out, RealizedPnL is populated. Failed: a Deploy
    /// attempt's broker order was rejected -- the strategy stays retryable (not Closed), this
    /// status is purely informational for the last attempt.</summary>
    public enum VerticalSpreadStatus
    {
        Paper,
        Live,
        Closed,
        Failed
    }

    public enum VerticalSpreadOrderAction
    {
        Open,
        Close
    }

    public enum VerticalSpreadOrderStatus
    {
        Submitted,
        Filled,
        Rejected,
        Failed
    }

    /// <summary>One manually-built vertical credit spread -- created via the dashboard builder
    /// (POST save), independent of StrategyInstances/StrategyOrders (see Schema.sql remarks:
    /// StrategyInstances is UNIQUE(StrategyType, Symbol), which would block the normal case of
    /// several concurrent spreads on the same underlying at different strikes/expirations; and
    /// a combo order's two legs don't fit StrategyOrders' single-symbol/single-side shape).</summary>
    public class VerticalSpreadStrategyRecord
    {
        public long Id { get; set; }
        public string Symbol { get; set; } = string.Empty;             // underlying, e.g. TQQQ
        public VerticalSpreadType SpreadType { get; set; }
        public OptionRight Right { get; set; }                          // Put (BullPutCredit) or Call (BearCallCredit)
        public DateTime ExpirationDate { get; set; }
        public decimal ShortStrike { get; set; }                        // the leg sold (collects premium)
        public decimal LongStrike { get; set; }                         // the leg bought (protection)
        public string ShortOptionSymbol { get; set; } = string.Empty;   // OCC symbol
        public string LongOptionSymbol { get; set; } = string.Empty;
        public int Contracts { get; set; }
        public decimal? ShortDeltaAtBuild { get; set; }                 // informational -- delta Tradier reported when this was built/saved
        public decimal? LongDeltaAtBuild { get; set; }
        public decimal NetCreditAtBuild { get; set; }                   // per-spread credit priced off live bid/ask at build time
        public decimal MaxRiskPerSpread { get; set; }                   // (Width - NetCreditAtBuild) * 100, informational
        public VerticalSpreadStatus Status { get; set; }
        public decimal? NetCreditReceived { get; set; }                 // actual combo-order fill price once Live; equals NetCreditAtBuild while Paper
        public DateTime OpenedUtc { get; set; }                         // when Saved (start of paper tracking)
        public DateTime? DeployedUtc { get; set; }                      // when the real Schwab combo order confirmed filled
        public DateTime? ClosedUtc { get; set; }
        public decimal? RealizedPnL { get; set; }
        public string? CloseReason { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public decimal Width => Math.Abs(ShortStrike - LongStrike);
    }

    /// <summary>Audit trail for the real broker-facing actions on a spread (open/close combo
    /// order) -- mirrors StrategyOrders' Submitted->Filled/Failed shape but scoped to a 2-leg
    /// combo order instead of a single symbol/side/quantity.</summary>
    public class VerticalSpreadOrderRecord
    {
        public long Id { get; set; }
        public long VerticalSpreadStrategyId { get; set; }
        public VerticalSpreadOrderAction ActionType { get; set; }
        public string LongOptionSymbol { get; set; } = string.Empty;
        public string ShortOptionSymbol { get; set; } = string.Empty;
        public int Contracts { get; set; }
        public decimal RequestedPrice { get; set; }                     // limit net credit (Open) / net debit (Close) sent to the broker
        public decimal? FillPrice { get; set; }
        public VerticalSpreadOrderStatus Status { get; set; }
        public string? BrokerOrderId { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime SubmittedUtc { get; set; }
        public DateTime? FilledUtc { get; set; }
    }

    /// <summary>Periodic mark-to-market snapshot (Paper AND Live) -- feeds the time-series P&amp;L
    /// chart and lets the marking job detect expiration without re-deriving history each tick.</summary>
    public class VerticalSpreadMarkRecord
    {
        public long Id { get; set; }
        public long VerticalSpreadStrategyId { get; set; }
        public DateTime MarkUtc { get; set; }
        public decimal UnderlyingPrice { get; set; }
        public decimal ShortMid { get; set; }
        public decimal LongMid { get; set; }
        public decimal SpreadMarkPrice { get; set; }                    // ShortMid - LongMid: current cost to close
        public decimal UnrealizedPnL { get; set; }
        public decimal? ShortDelta { get; set; }
        public decimal? LongDelta { get; set; }
        public decimal? NetDelta { get; set; }
        public int DaysToExpiration { get; set; }
    }
}
