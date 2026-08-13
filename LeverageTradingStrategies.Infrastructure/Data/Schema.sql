-- Leverage-Trading-Strategies schema. Applied automatically at startup by
-- DatabaseInitializer (idempotent -- CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT
-- EXISTS), matching MarketMatrixPreparer's own convention of hand-written SQL for schema
-- changes rather than EF Core migrations.
--
-- Designed to host more than one strategy: StrategyType + Symbol identify an instance, so
-- a future 'OptionsSeller' strategy (ported from MarketMatrixPreparer, not yet done here --
-- see project README) can reuse StrategyInstances and StrategyOrders as-is and just add its
-- own strategy-specific state table alongside TqqqWeeklyStates.

CREATE TABLE IF NOT EXISTS StrategyInstances (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StrategyType TEXT NOT NULL,            -- 'TqqqWeekly' (future: 'OptionsSeller')
    Symbol TEXT NOT NULL,
    AllocatedCapital REAL NOT NULL,        -- base capital this instance is configured to deploy
    CompoundingEnabled INTEGER NOT NULL DEFAULT 0,   -- 1 = realized P&L rolls into CurrentCapital for next sizing decision; 0 = always size off AllocatedCapital
    CurrentCapital REAL NOT NULL,          -- AllocatedCapital, plus cumulative realized P&L if CompoundingEnabled
    Status TEXT NOT NULL DEFAULT 'Running',          -- Running | Paused | Killed
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL,
    UNIQUE (StrategyType, Symbol)
);

CREATE TABLE IF NOT EXISTS TqqqWeeklyStates (
    StrategyInstanceId INTEGER PRIMARY KEY REFERENCES StrategyInstances(Id),
    Holding INTEGER NOT NULL DEFAULT 0,
    EntryPrice REAL NOT NULL DEFAULT 0,
    Quantity INTEGER NOT NULL DEFAULT 0,
    TotalCostBasis REAL NOT NULL DEFAULT 0,          -- true weighted cost basis (entry + any avg-down adds), independent of the decision-layer's EntryPrice reset quirk -- used for accurate RealizedPnL on exit
    EntryDate TEXT NULL,
    EnteredOnMonday INTEGER NOT NULL DEFAULT 0,
    AddedThisPosition INTEGER NOT NULL DEFAULT 0,
    MondayAvgDownWindowConsumed INTEGER NOT NULL DEFAULT 0,
    CurrentTargetPrice REAL NOT NULL DEFAULT 0,
    CurrentIsoWeekKey INTEGER NULL,
    TradedThisWeek INTEGER NOT NULL DEFAULT 0,
    DeployGuardConsumed INTEGER NOT NULL DEFAULT 0,
    HasEverRun INTEGER NOT NULL DEFAULT 0,
    RecentDailyClosesJson TEXT NOT NULL DEFAULT '[]',
    VolHistoryJson TEXT NOT NULL DEFAULT '[]',
    VolGateClosedToday INTEGER NOT NULL DEFAULT 0,
    LastSessionOpenDate TEXT NULL,
    LastForceCloseCheckDate TEXT NULL,
    LastSessionCloseDate TEXT NULL,
    LastVolRollDate TEXT NULL,
    LastUpdatedUtc TEXT NULL
);

CREATE TABLE IF NOT EXISTS StrategyOrders (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StrategyInstanceId INTEGER NOT NULL REFERENCES StrategyInstances(Id),
    Symbol TEXT NOT NULL,
    ActionType TEXT NOT NULL,              -- EnterLong | AddToPosition | SellAll
    Side TEXT NOT NULL,                    -- Buy | Sell
    Quantity INTEGER NOT NULL,
    Reason TEXT NOT NULL,
    Status TEXT NOT NULL,                  -- Submitted | Filled | Failed
    IsSimulated INTEGER NOT NULL,
    RequestedPrice REAL NULL,
    FillPrice REAL NULL,
    RealizedPnL REAL NULL,                 -- populated on a filled SellAll only
    BrokerOrderId TEXT NULL,
    ErrorMessage TEXT NULL,
    SubmittedUtc TEXT NOT NULL,
    FilledUtc TEXT NULL
);

CREATE INDEX IF NOT EXISTS IX_StrategyOrders_StrategyInstanceId_SubmittedUtc
    ON StrategyOrders (StrategyInstanceId, SubmittedUtc DESC);

-- Generic per-instance tuning-parameter store (key/value). Same pattern MarketMatrixPreparer
-- already uses for its AppConfig table -- adding a new tunable never needs a schema change.
-- Seeded once per instance from appsettings.json defaults (AppSettingsOptions.TqqqWeekly) the
-- first time TqqqWeeklyConfigProvider.GetAsync runs for that instance (INSERT OR IGNORE, so it
-- never clobbers a value already tuned here); after that, this table is the source of truth --
-- edit a row and it takes effect on the very next job tick, no app restart required. Works for
-- any future strategy type too (StrategyInstanceId is the only strategy-specific link).
CREATE TABLE IF NOT EXISTS StrategyConfig (
    StrategyInstanceId INTEGER NOT NULL REFERENCES StrategyInstances(Id),
    Key TEXT NOT NULL,
    Value TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL,
    PRIMARY KEY (StrategyInstanceId, Key)
);

-- Vertical credit spreads (manually built in the dashboard: symbol, expiration, strikes) get
-- their OWN table family rather than reusing StrategyInstances/StrategyOrders above -- several
-- concurrent spreads on the same underlying at different strikes/expirations is the NORMAL case
-- here, which conflicts with StrategyInstances' UNIQUE(StrategyType, Symbol); and a combo
-- order's two legs don't fit StrategyOrders' single-symbol/single-side shape.
CREATE TABLE IF NOT EXISTS VerticalSpreadStrategies (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Symbol TEXT NOT NULL,                  -- underlying, e.g. TQQQ
    SpreadType TEXT NOT NULL,              -- BullPutCredit | BearCallCredit
    OptionRight TEXT NOT NULL,             -- Put | Call
    ExpirationDate TEXT NOT NULL,          -- yyyy-MM-dd
    ShortStrike REAL NOT NULL,
    LongStrike REAL NOT NULL,
    ShortOptionSymbol TEXT NOT NULL,       -- OCC symbol
    LongOptionSymbol TEXT NOT NULL,
    Contracts INTEGER NOT NULL,
    ShortDeltaAtBuild REAL NULL,
    LongDeltaAtBuild REAL NULL,
    NetCreditAtBuild REAL NOT NULL,        -- per-spread credit priced off live bid/ask when built/saved
    MaxRiskPerSpread REAL NOT NULL,        -- (Width - NetCreditAtBuild) * 100, informational
    Status TEXT NOT NULL DEFAULT 'Paper',  -- Paper | Live | Closed | Failed
    NetCreditReceived REAL NULL,           -- actual combo-order fill once Live; equals NetCreditAtBuild while Paper
    OpenedUtc TEXT NOT NULL,               -- when Saved -- start of paper tracking
    DeployedUtc TEXT NULL,                 -- when the real Schwab combo order confirmed filled
    ClosedUtc TEXT NULL,
    RealizedPnL REAL NULL,
    CloseReason TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_VerticalSpreadStrategies_Status ON VerticalSpreadStrategies (Status);

-- Order audit trail for the real broker-facing actions on a spread (open/close combo order) --
-- mirrors StrategyOrders' Submitted->Filled/Failed shape but scoped to a 2-leg combo order.
CREATE TABLE IF NOT EXISTS VerticalSpreadOrders (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    VerticalSpreadStrategyId INTEGER NOT NULL REFERENCES VerticalSpreadStrategies(Id),
    ActionType TEXT NOT NULL,              -- Open | Close
    LongOptionSymbol TEXT NOT NULL,
    ShortOptionSymbol TEXT NOT NULL,
    Contracts INTEGER NOT NULL,
    RequestedPrice REAL NOT NULL,          -- limit net credit (Open) / net debit (Close) sent to the broker
    FillPrice REAL NULL,
    Status TEXT NOT NULL,                  -- Submitted | Filled | Rejected | Failed
    BrokerOrderId TEXT NULL,
    ErrorMessage TEXT NULL,
    SubmittedUtc TEXT NOT NULL,
    FilledUtc TEXT NULL
);

CREATE INDEX IF NOT EXISTS IX_VerticalSpreadOrders_StrategyId_SubmittedUtc
    ON VerticalSpreadOrders (VerticalSpreadStrategyId, SubmittedUtc DESC);

-- Periodic mark-to-market snapshots (Paper AND Live) -- feeds the time-series P&L chart and lets
-- the marking job detect expiration without re-deriving history each tick.
CREATE TABLE IF NOT EXISTS VerticalSpreadMarks (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    VerticalSpreadStrategyId INTEGER NOT NULL REFERENCES VerticalSpreadStrategies(Id),
    MarkUtc TEXT NOT NULL,
    UnderlyingPrice REAL NOT NULL,
    ShortMid REAL NOT NULL,
    LongMid REAL NOT NULL,
    SpreadMarkPrice REAL NOT NULL,         -- ShortMid - LongMid: current cost to close
    UnrealizedPnL REAL NOT NULL,
    ShortDelta REAL NULL,
    LongDelta REAL NULL,
    NetDelta REAL NULL,
    DaysToExpiration INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_VerticalSpreadMarks_StrategyId_MarkUtc
    ON VerticalSpreadMarks (VerticalSpreadStrategyId, MarkUtc DESC);

-- TQQQ intraday discretionary agent (see TQQQ_Intraday_Agent_Spec_v1.md at repo root). Single
-- instrument (TQQQ), single live account -- deliberately NOT modeled as a StrategyInstances row;
-- this module is fully isolated from the shared strategy framework (see spec's architecture
-- notes) so a bug here can't touch TqqqWeekly/VerticalSpread/OptionsSeller state.

-- One row per decision cycle (default every 5 minutes during market hours) -- the durable audit
-- trail the whole module is built around. Snapshot columns are the exact JSON handed to/read from
-- Claude that cycle, kept verbatim so a past decision can be reviewed without reconstructing
-- market conditions from other tables. FinalAction/Shares/Approved reflect what the validator (9
-- hard-limit checks, see spec §7) actually allowed -- may differ from RawAction/RawConfidence/RawWhy,
-- which is Claude's unmodified submit_decision call.
CREATE TABLE IF NOT EXISTS TqqqAgentDecisions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CycleUtc TEXT NOT NULL,
    PortfolioSnapshotJson TEXT NOT NULL,
    MarketSnapshotJson TEXT NOT NULL,
    RawAction TEXT NOT NULL,               -- Hold | Buy | Sell, as Claude submitted it
    RawConfidence REAL NOT NULL,
    RawWhy TEXT NOT NULL,
    Approved INTEGER NOT NULL,             -- 1 if the validator allowed FinalAction to proceed to an order attempt
    FinalAction TEXT NOT NULL,             -- Hold | Buy | Sell, after validator overrides (e.g. forced end-of-day Sell)
    Shares INTEGER NOT NULL,
    RejectReason TEXT NULL,                -- set when Approved=0, or when a Sell/Buy was downgraded to a no-op Hold
    OrderStatus TEXT NOT NULL DEFAULT 'None',  -- None | Submitted | Filled | Failed
    BrokerOrderId TEXT NULL,
    FillPrice REAL NULL,
    RealizedPnL REAL NULL,                 -- populated on a filled Sell only
    ErrorMessage TEXT NULL
);

CREATE INDEX IF NOT EXISTS IX_TqqqAgentDecisions_CycleUtc
    ON TqqqAgentDecisions (CycleUtc DESC);

-- One row per Eastern trading date, written once (first cycle of the day) and never updated
-- after that -- the fixed denominator for the daily loss-stop check (spec §7 check 6), which
-- must NOT drift as TotalEquity moves intraday with today's own realized P&L.
CREATE TABLE IF NOT EXISTS TqqqAgentDailyState (
    TradeDateEt TEXT PRIMARY KEY,          -- yyyy-MM-dd, Eastern
    DayStartEquity REAL NOT NULL,
    CreatedUtc TEXT NOT NULL
);

-- Single-row (Id=1 enforced by CHECK) manual control surface for the kill/pause endpoints (spec
-- task: controller status/kill/pause/resume). IsKilled stops the job from doing anything at all
-- (including calling Claude); IsPaused still runs forced-flatten/risk logic on any open position
-- but blocks new entries -- same Running/Paused/Killed distinction TqqqWeeklyLiveTradingJob uses
-- via StrategyInstances.Status, just single-row since there's only ever one TQQQ agent instance.
CREATE TABLE IF NOT EXISTS TqqqAgentControlState (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    IsKilled INTEGER NOT NULL DEFAULT 0,
    IsPaused INTEGER NOT NULL DEFAULT 0,
    Reason TEXT NULL,
    UpdatedUtc TEXT NOT NULL
);

INSERT OR IGNORE INTO TqqqAgentControlState (Id, IsKilled, IsPaused, Reason, UpdatedUtc)
    VALUES (1, 0, 0, NULL, '1970-01-01T00:00:00.0000000Z');
