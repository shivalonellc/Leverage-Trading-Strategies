# TQQQ Intraday Agent — Design Spec v1

**Status: CONFIRMED — implementing now. Sizing (§8), risk limits (§7), forced-flatten time (§4/§7),
and live-from-day-one (§1) are all signed off. Implement exactly this, nothing more, nothing less,
until we explicitly change it.**

## 1. Objective

A new, standalone Quartz job in this solution that runs an intraday, day-trading-only, long-only
strategy on TQQQ. Every cycle, Claude reasons freely over live portfolio + market data (no
strategy rules baked into the prompt) and returns a decision; deterministic code then validates,
sizes, and executes it against the **live Tradier account**. No paper/sandbox mode — this trades
real money from cycle one.

## 2. Architecture — who decides what

```
Quartz trigger (every N minutes, market hours)
        │
        ▼
  get_portfolio ──────────┐
  get_market_snapshot ────┤──▶  Claude (Anthropic Messages API call,
  get_recent_decisions ───┘      NOT this chat session — see §9)
        │
        ▼
  submit_decision  { action: Buy|Sell|Hold, confidence: 0-1, why: string }
════════════════════ Claude's authority ends here ════════════════════
        ▼
  Validator (9 hard-limit checks, §7) — code only, no LLM
        ▼
  Position sizing (§8) — code only
        ▼
  Order placed directly against Tradier's live order endpoint — code only,
  never through a Claude-invoked tool call
```

The job itself calls the Anthropic API directly (a plain HTTP call from C#, using an API key you
provide — see §11), exposing only the three tools above. This is a different Claude than the one
you're talking to in this chat; that's what makes unattended 5-minute-interval execution possible.

## 3. Instrument & account

- Symbol: **TQQQ only**, hard-locked in the validator.
- Account: live Tradier account #REDACTED_TRADIER_ACCOUNT_ID (current equity $511.05, no existing position).
- **Long only** — actions are `Buy` (open long), `Sell` (close long), `Hold`. No short selling,
  ever. The validator rejects anything else regardless of what Claude returns.
- **Day-trade only** — at most one position open at a time, opened and closed same day. No
  overnight holds. A forced flatten fires near market close regardless of the day's last decision
  (§7, check 4).
- No averaging/adding to a position — if already long, the only live choices are Sell or Hold.

## 4. Decision cycle

- Interval: **configurable, default 5 minutes** (`AppSettings:TqqqAgent:DecisionIntervalMinutes`).
- Only runs during market hours. Proposed window: new-entry decisions from **9:35–15:40 ET**
  (skips the first 5 min of open-auction noise and the run-up to the forced-flatten cutover at
  15:45). A separate always-fires check handles the forced close (§7, check 4).
- Each fire is one full cycle: fetch → decide → validate → size → execute → log → update Redis.

## 5. What Claude sees each cycle

**`get_portfolio`** — current TQQQ position (flat or qty+cost basis), cash/buying power, today's
realized P&L so far, today's consecutive-loss count, whether a halt is currently active and why.

**`get_market_snapshot`** — precomputed, not raw candles:
- TQQQ: last price, VWAP and price-vs-VWAP, EMA9/EMA20, RSI(14), MACD + histogram, ATR(14),
  today's high/low so far, gap from prior close, distance from today's open, volume vs. same
  time-of-day average.
- QQQ (regime context, since TQQQ is a 3x derivative of it): last price, trend vs VWAP/EMA20.
- Time context: minutes since open, minutes until the forced-flatten cutover.

**`get_recent_decisions`** — the last **10** decisions from Redis (timestamp, action, confidence,
why, and outcome/realized P&L once known), so Claude has continuity across otherwise-stateless
API calls instead of re-deciding from zero every 5 minutes.

## 6. The decision itself

No strategy rules in the prompt — Claude gets the data above and full discretion to reason about
whether there's a good long setup, whether to hold, or whether to exit an open position, and
returns `{ action, confidence, why }` via `submit_decision`. `confidence` is always logged; it is
**not** a hard gate by default (you were clear you don't want rule-based constraints on the
decision itself) — but I'm adding a configurable optional floor
(`AppSettings:TqqqAgent:MinConfidenceToAct`, default `0` = disabled) so you can turn one on later
without a code change if impulsive-looking trades become a problem.

## 7. Validator — 9 hard-limit checks (code only, run after every decision)

1. Symbol must be exactly `TQQQ` — reject anything else outright.
2. Action must be `Buy`, `Sell`, or `Hold` and long-only (no short/sell-to-open).
3. No new `Buy` if a position is already open (one position at a time, no pyramiding).
4. **Forced flatten**: if it's past the cutover time (**15:45 ET**) and a position is open, force
   a `Sell` regardless of what Claude decided that cycle (or didn't get asked, if the job's last
   regular cycle already passed).
5. **3-consecutive-losses circuit breaker**: 3 losing round-trip trades in the same day blocks
   any new `Buy` for the rest of that day. An already-open position can still be closed.
6. **Daily loss stop**: cumulative realized P&L for the day ≤ **-5% of that day's starting
   equity** (≈ -$25 at current balance) blocks any new `Buy` for the rest of the day.
7. **Position sizing ceiling** (§8) — computed size is capped; if it rounds to 0 shares, the
   trade is skipped and logged rather than sent.
8. **Market-hours gate** — no new entries outside 9:35–15:40 ET; no action at all outside
   9:30–16:00 ET.
9. **Kill switch** — a Running/Paused/Killed status flag (same convention as your other live
   jobs, with matching controller endpoints) checked before every single order, independent of
   all the above.

## 8. Position sizing

Your $511 equity makes a conventional "small % of equity per trade" cap mathematically unusable
(10% ≈ $51 ≈ 0 shares of a $76.80 stock). Proposed formula instead:

```
maxNotional = min(availableCash × EquityUsageFraction, MaxNotionalCeiling)
shares = floor(maxNotional / currentPrice)
```

Proposed defaults: `EquityUsageFraction = 0.85`, `MaxNotionalCeiling = $450`. At today's price
(~$76.80) and full cash, that's ~5 shares (~$384), leaving a buffer for slippage. Both values are
config, not code.

## 9. Order execution

Once validated and sized, the job calls Tradier's live order-placement REST endpoint **directly**
from C# (using your existing live Tradier token from `AppSettings:Tradier`), the same way
`VerticalSpreadOrderExecutor` already does for the options module. This is not routed through any
Claude-invoked tool — it's plain application code, matching the "Code decides" half of your
diagram exactly.

## 10. Persistence

- **SQLite** (source of truth, survives restarts): a new `TqqqAgentDecisions` table — one row per
  cycle (timestamp, portfolio snapshot, market snapshot, decision, validator outcome, order
  result, fill price, realized P&L when closed) — plus running per-day counters (consecutive
  losses, realized P&L) derived from it. Mirrors the `VerticalSpreadOrders`/`VerticalSpreadMarks`
  schema style already in this project.
- **Redis** (fast cache only, rebuildable from SQLite): the last 10 decisions under key prefix
  `tqqqagent:`, reusing the same `localhost:6379` instance already wired up for the options
  module's chain cache.

## 11. Config additions needed

- `AppSettings:TqqqAgent` section: `Enabled`, `DecisionIntervalMinutes` (5), `EquityUsageFraction`
  (0.85), `MaxNotionalCeiling` (450), `DailyLossStopPct` (0.05), `ConsecutiveLossLimit` (3),
  `ForceFlattenHourEt`/`MinuteEt` (15:45), `MinConfidenceToAct` (0, disabled).
- **New requirement**: an Anthropic API key (from console.anthropic.com — separate from your
  Claude.ai/Cowork access) stored the same way your other secrets are (`AppSettings` in
  `appsettings.json`). Placeholder will be added; you fill in the real value directly in the file
  (never pasted into chat) before enabling the job.

## 12. Sign-off (confirmed 2026-08-13)

1. Sizing/risk numbers in §7/§8 — **confirmed as proposed**.
2. Forced-flatten time — **15:45 ET** (moved earlier than the original 15:55 proposal for extra
   buffer).
3. Anthropic API key — **user will provide**; a placeholder + fill-in instructions will be added
   to `appsettings.json`.
4. Live from day one, no sandbox — **confirmed** (restated multiple times in chat).
