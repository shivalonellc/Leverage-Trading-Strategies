using LeverageTradingStrategies.Infrastructure.Data;
using LeverageTradingStrategies.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace LeverageTradingStrategies.Infrastructure.State
{
    /// <summary>
    /// SQLite-backed state store — one row per StrategyInstanceId in TqqqWeeklyStates.
    /// Replaces the earlier JSON-file-per-symbol store now that persistence needs to sit
    /// alongside order history and capital tracking in the same database. Raw ADO.NET
    /// (Microsoft.Data.Sqlite), same convention as the other repositories in Data/ —
    /// deliberately not EF, see the PackageReference comment in the .csproj for why.
    /// </summary>
    public class SqliteTqqqWeeklyStateStore : ITqqqWeeklyStateStore
    {
        private readonly ISqliteConnectionFactory _connectionFactory;
        private static readonly SemaphoreSlim Lock = new(1, 1);

        public SqliteTqqqWeeklyStateStore(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<TqqqWeeklyState> GetOrCreateAsync(int strategyInstanceId, string symbol, CancellationToken ct = default)
        {
            await Lock.WaitAsync(ct);
            try
            {
                using var conn = _connectionFactory.CreateOpenConnection();
                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.CommandText = """
                        SELECT Holding, EntryPrice, Quantity, TotalCostBasis, EntryDate, EnteredOnMonday,
                               AddedThisPosition, MondayAvgDownWindowConsumed, CurrentTargetPrice, CurrentIsoWeekKey,
                               TradedThisWeek, DeployGuardConsumed, HasEverRun, RecentDailyClosesJson, VolHistoryJson,
                               VolGateClosedToday, LastSessionOpenDate, LastForceCloseCheckDate, LastSessionCloseDate,
                               LastVolRollDate, LastUpdatedUtc
                        FROM TqqqWeeklyStates
                        WHERE StrategyInstanceId = @Id;
                        """;
                    selectCmd.Parameters.AddWithValue("@Id", strategyInstanceId);
                    using var reader = await selectCmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        return ReadState(reader, symbol);
                    }
                }

                // No row yet -- insert a fresh default state row for this instance.
                var fresh = new TqqqWeeklyState { Symbol = symbol.ToUpperInvariant() };
                using (var insertCmd = conn.CreateCommand())
                {
                    insertCmd.CommandText = """
                        INSERT INTO TqqqWeeklyStates (StrategyInstanceId, Holding, EntryPrice, Quantity, TotalCostBasis,
                            EnteredOnMonday, AddedThisPosition, MondayAvgDownWindowConsumed, CurrentTargetPrice,
                            TradedThisWeek, DeployGuardConsumed, HasEverRun, RecentDailyClosesJson, VolHistoryJson,
                            VolGateClosedToday)
                        VALUES (@Id, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '[]', '[]', 0);
                        """;
                    insertCmd.Parameters.AddWithValue("@Id", strategyInstanceId);
                    await insertCmd.ExecuteNonQueryAsync(ct);
                }
                return fresh;
            }
            finally
            {
                Lock.Release();
            }
        }

        public async Task SaveAsync(int strategyInstanceId, TqqqWeeklyState state, CancellationToken ct = default)
        {
            state.LastUpdatedUtc = DateTime.UtcNow;
            await Lock.WaitAsync(ct);
            try
            {
                using var conn = _connectionFactory.CreateOpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE TqqqWeeklyStates SET
                        Holding = @Holding, EntryPrice = @EntryPrice, Quantity = @Quantity, TotalCostBasis = @TotalCostBasis,
                        EntryDate = @EntryDate, EnteredOnMonday = @EnteredOnMonday, AddedThisPosition = @AddedThisPosition,
                        MondayAvgDownWindowConsumed = @MondayAvgDownWindowConsumed, CurrentTargetPrice = @CurrentTargetPrice,
                        CurrentIsoWeekKey = @CurrentIsoWeekKey, TradedThisWeek = @TradedThisWeek,
                        DeployGuardConsumed = @DeployGuardConsumed, HasEverRun = @HasEverRun,
                        RecentDailyClosesJson = @RecentDailyClosesJson, VolHistoryJson = @VolHistoryJson,
                        VolGateClosedToday = @VolGateClosedToday, LastSessionOpenDate = @LastSessionOpenDate,
                        LastForceCloseCheckDate = @LastForceCloseCheckDate, LastSessionCloseDate = @LastSessionCloseDate,
                        LastVolRollDate = @LastVolRollDate, LastUpdatedUtc = @LastUpdatedUtc
                    WHERE StrategyInstanceId = @Id;
                    """;
                cmd.Parameters.AddWithValue("@Holding", state.Holding ? 1 : 0);
                cmd.Parameters.AddWithValue("@EntryPrice", state.EntryPrice);
                cmd.Parameters.AddWithValue("@Quantity", state.Quantity);
                cmd.Parameters.AddWithValue("@TotalCostBasis", state.TotalCostBasis);
                cmd.Parameters.AddWithValue("@EntryDate", (object?)state.EntryDate?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@EnteredOnMonday", state.EnteredOnMonday ? 1 : 0);
                cmd.Parameters.AddWithValue("@AddedThisPosition", state.AddedThisPosition ? 1 : 0);
                cmd.Parameters.AddWithValue("@MondayAvgDownWindowConsumed", state.MondayAvgDownWindowConsumed ? 1 : 0);
                cmd.Parameters.AddWithValue("@CurrentTargetPrice", state.CurrentTargetPrice);
                cmd.Parameters.AddWithValue("@CurrentIsoWeekKey", (object?)state.CurrentIsoWeekKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TradedThisWeek", state.TradedThisWeek ? 1 : 0);
                cmd.Parameters.AddWithValue("@DeployGuardConsumed", state.DeployGuardConsumed ? 1 : 0);
                cmd.Parameters.AddWithValue("@HasEverRun", state.HasEverRun ? 1 : 0);
                cmd.Parameters.AddWithValue("@RecentDailyClosesJson", JsonSerializer.Serialize(state.RecentDailyCloses));
                cmd.Parameters.AddWithValue("@VolHistoryJson", JsonSerializer.Serialize(state.VolHistory));
                cmd.Parameters.AddWithValue("@VolGateClosedToday", state.VolGateClosedToday ? 1 : 0);
                cmd.Parameters.AddWithValue("@LastSessionOpenDate", (object?)state.LastSessionOpenDate?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@LastForceCloseCheckDate", (object?)state.LastForceCloseCheckDate?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@LastSessionCloseDate", (object?)state.LastSessionCloseDate?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@LastVolRollDate", (object?)state.LastVolRollDate?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@LastUpdatedUtc", state.LastUpdatedUtc?.ToString("O") ?? DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@Id", strategyInstanceId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                Lock.Release();
            }
        }

        private static TqqqWeeklyState ReadState(SqliteDataReader reader, string symbol)
        {
            return new TqqqWeeklyState
            {
                Symbol = symbol.ToUpperInvariant(),
                Holding = reader.GetInt32(0) != 0,
                EntryPrice = reader.GetDecimal(1),
                Quantity = reader.GetInt32(2),
                TotalCostBasis = reader.GetDecimal(3),
                EntryDate = reader.IsDBNull(4) ? null : DateOnly.Parse(reader.GetString(4)),
                EnteredOnMonday = reader.GetInt32(5) != 0,
                AddedThisPosition = reader.GetInt32(6) != 0,
                MondayAvgDownWindowConsumed = reader.GetInt32(7) != 0,
                CurrentTargetPrice = reader.GetDecimal(8),
                CurrentIsoWeekKey = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                TradedThisWeek = reader.GetInt32(10) != 0,
                DeployGuardConsumed = reader.GetInt32(11) != 0,
                HasEverRun = reader.GetInt32(12) != 0,
                RecentDailyCloses = JsonSerializer.Deserialize<List<decimal>>(reader.GetString(13)) ?? new(),
                VolHistory = JsonSerializer.Deserialize<List<double>>(reader.GetString(14)) ?? new(),
                VolGateClosedToday = reader.GetInt32(15) != 0,
                LastSessionOpenDate = reader.IsDBNull(16) ? null : DateOnly.Parse(reader.GetString(16)),
                LastForceCloseCheckDate = reader.IsDBNull(17) ? null : DateOnly.Parse(reader.GetString(17)),
                LastSessionCloseDate = reader.IsDBNull(18) ? null : DateOnly.Parse(reader.GetString(18)),
                LastVolRollDate = reader.IsDBNull(19) ? null : DateOnly.Parse(reader.GetString(19)),
                LastUpdatedUtc = reader.IsDBNull(20) ? null : DateTime.Parse(reader.GetString(20)).ToUniversalTime()
            };
        }
    }
}
