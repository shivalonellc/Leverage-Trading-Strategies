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
