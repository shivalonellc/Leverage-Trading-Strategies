using Microsoft.Extensions.Logging;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    /// <summary>
    /// Idempotently creates the schema on startup (CREATE TABLE/INDEX IF NOT EXISTS — safe to
    /// run every time the app starts). The SQL here is intentionally kept IDENTICAL to
    /// Data/Schema.sql (that file is the human-readable reference copy for anyone applying it
    /// by hand / reviewing schema changes) — duplicated as a plain string constant rather than
    /// read from disk or an embedded resource so there's nothing that can fail to resolve at
    /// runtime. If you change one, change the other.
    /// </summary>
    public class DatabaseInitializer
    {
        private readonly ISqliteConnectionFactory _connectionFactory;
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(ISqliteConnectionFactory connectionFactory, ILogger<DatabaseInitializer> logger)
        {
            _connectionFactory = connectionFactory;
            _logger = logger;
        }

        private const string SchemaSql = """
            CREATE TABLE IF NOT EXISTS StrategyInstances (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StrategyType TEXT NOT NULL,
                Symbol TEXT NOT NULL,
                AllocatedCapital REAL NOT NULL,
                CompoundingEnabled INTEGER NOT NULL DEFAULT 0,
                CurrentCapital REAL NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Running',
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (StrategyType, Symbol)
            );

            CREATE TABLE IF NOT EXISTS TqqqWeeklyStates (
                StrategyInstanceId INTEGER PRIMARY KEY REFERENCES StrategyInstances(Id),
                Holding INTEGER NOT NULL DEFAULT 0,
                EntryPrice REAL NOT NULL DEFAULT 0,
                Quantity INTEGER NOT NULL DEFAULT 0,
                TotalCostBasis REAL NOT NULL DEFAULT 0,
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
                ActionType TEXT NOT NULL,
                Side TEXT NOT NULL,
                Quantity INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                Status TEXT NOT NULL,
                IsSimulated INTEGER NOT NULL,
                RequestedPrice REAL NULL,
                FillPrice REAL NULL,
                RealizedPnL REAL NULL,
                BrokerOrderId TEXT NULL,
                ErrorMessage TEXT NULL,
                SubmittedUtc TEXT NOT NULL,
                FilledUtc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_StrategyOrders_StrategyInstanceId_SubmittedUtc
                ON StrategyOrders (StrategyInstanceId, SubmittedUtc DESC);

            CREATE TABLE IF NOT EXISTS StrategyConfig (
                StrategyInstanceId INTEGER NOT NULL REFERENCES StrategyInstances(Id),
                Key TEXT NOT NULL,
                Value TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (StrategyInstanceId, Key)
            );
            """;

        public void EnsureCreated()
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SchemaSql;
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Database schema verified/created");
        }
    }
}
