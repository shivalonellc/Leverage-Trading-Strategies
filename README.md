# Leverage-Trading-Strategies

.NET solution for live/paper-traded leveraged-ETF strategies, starting with the TQQQ weekly
strategy (verified backtest: 49.9% CAGR / 22.1% max DD, 2010-02-11 to 2026-08-07 — see
`TQQQ_Weekly_Strategy_Spec_v1.md` in the MarketMatrixPreparer repo for the full rule-by-rule
spec this code implements).

## ⚠️ Build status — read this first

This scaffold was authored in a sandboxed environment **without a local .NET SDK** (network
access to install one was blocked), so **none of this code has been compiled yet**. The
structure, DI wiring, and method signatures were checked by hand against the working patterns
already proven in the MarketMatrixPreparer repo (IBroker/SchwabBroker were copied verbatim),
but there has been no `dotnet build` pass.

**First step on your machine: run `dotnet build` and report back any errors** — they'll almost
certainly be small (a missing `using`, a package version mismatch) and quick to fix.

```
cd Leverage-Trading-Strategies
dotnet restore
dotnet build
```

## Structure

- `LeverageTradingStrategies.Domain/Tqqq/` — the strategy's decision logic
  (`TqqqWeeklyStrategyService`), pure and unit-testable: given state + a quote + portfolio
  value, returns what action (if any) to take. Mirrors the Python backtest's `run_sim` rules
  exactly (entry sizing + vol gate, tiered take-profit, standard + Monday-special avg-down,
  force-close-weekly, close-based stop, EOW backstop).
- `LeverageTradingStrategies.Infrastructure/`
  - `Brokers/` — `IBroker`, `SchwabBroker` (copied from MarketMatrixPreparer, unmodified logic
    plus a new `GetPortfolioValueAsync`), `SimulatedBroker` (in-memory, for dry-run smoke
    testing only — **not** a backtest engine).
  - `Quotes/` — `IQuoteProvider` / `SchwabQuoteProvider`: a lightweight quote poll (open/high/
    low/last/prev-close) instead of the full 1-min-bar ingestion pipeline — a weekly-cadence
    strategy doesn't need Renko-level granularity.
  - `State/` — `ITqqqWeeklyStateStore` / `JsonFileTqqqWeeklyStateStore`: durable per-symbol
    state as a flat JSON file (not EF/SQLite for v1 — see the class remarks for why).
  - `Models/TqqqWeeklyState.cs` — the persisted state shape.
  - `Configuration/AppSettingsOptions.cs` — every strategy parameter, defaulted to the
    verified baseline values.
- `LeverageTradingStrategies.Api/`
  - `Jobs/TqqqWeeklyLiveTradingJob.cs` — the Quartz job that ticks during market hours and
    drives the strategy.
  - `Controllers/TqqqWeeklyController.cs` — `GET /api/tqqq-weekly/status`.
  - `Program.cs` — DI/Quartz/Serilog wiring.
- `SchwabApiCS/` — copied wholesale from MarketMatrixPreparer (the Schwab REST/order client).

## Before going live

1. **Fix whatever `dotnet build` surfaces** (see above).
2. Set `AppSettings:Trading:AccountNumber` and confirm `AppSettings:Trading:SchwabTokenPath`
   points at a real, working token file.
3. Leave `AppSettings:Trading:UseSimulatedBroker: true` and `AppSettings:TqqqWeekly:Enabled:
   false` and run the app — confirm the `/api/tqqq-weekly/status` endpoint responds and the
   job ticks without exceptions (check `logs/`).
4. Flip `Enabled: true` with the simulated broker still on, watch a few days of paper decisions
   in the logs, and sanity-check them against what you'd expect by hand.
5. Read the known-gaps list before flipping `UseSimulatedBroker` to `false`:
   - **No fill confirmation / reconciliation.** Orders are placed and state is updated
     optimistically (matching the backtest's "best-case fill" assumption). If a real order is
     rejected or fills at a very different price, state can drift from the real broker
     position until someone notices. Recommended hardening: fill confirmation + reconciliation
     against `IBroker.GetSymbolPositionAsync`, same pattern already used for options-seller
     order confirmation in MarketMatrixPreparer.
   - **No holiday calendar.** "Day before last trading day of week" / "last trading day of
     week" are approximated as Thursday/Friday. The EOW backstop is the safety net for
     holiday-shortened weeks, but hasn't been tested against one.
   - **Avg-down entry-price-reset quirk not fully re-verified.** The Python backtest reset
     `entry_price` to the fill price on every buy including avg-downs (not a blended average).
     This C# port implements that same reset, but the exact same-day ordering versus the
     tier-target recompute should be checked line-by-line against
     `fleury_tqqq_weekly_replication.py` before trusting it with real capital — see the remarks
     on `TqqqWeeklyStrategyService`.
   - **No margin cap.** Matches the verified baseline (unlimited margin), but real accounts
     have real margin limits — an avg-down could get rejected by the broker if you're not
     tracking buying power. Not wired in yet.
6. This repo is not yet pushed to GitHub — create an empty repo (no README/gitignore) and:
   ```
   git remote add origin <your-repo-url>
   git branch -M main
   git push -u origin main
   ```
