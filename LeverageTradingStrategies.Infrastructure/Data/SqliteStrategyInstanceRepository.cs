using LeverageTradingStrategies.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public class SqliteStrategyInstanceRepository : IStrategyInstanceRepository
    {
        private readonly ISqliteConnectionFactory _connectionFactory;

        public SqliteStrategyInstanceRepository(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<StrategyInstanceRecord> GetOrCreateAsync(string strategyType, string symbol, decimal seedAllocatedCapital, bool seedCompoundingEnabled, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();

            var existing = await SelectByTypeAndSymbolAsync(conn, strategyType, symbol, ct);
            if (existing != null)
                return existing;

            var now = DateTime.UtcNow;
            using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.CommandText = """
                    INSERT INTO StrategyInstances
                        (StrategyType, Symbol, AllocatedCapital, CompoundingEnabled, CurrentCapital, Status, CreatedUtc, UpdatedUtc)
                    VALUES
                        (@StrategyType, @Symbol, @AllocatedCapital, @CompoundingEnabled, @CurrentCapital, @Status, @CreatedUtc, @UpdatedUtc);
                    SELECT last_insert_rowid();
                    """;
                insertCmd.Parameters.AddWithValue("@StrategyType", strategyType);
                insertCmd.Parameters.AddWithValue("@Symbol", symbol);
                insertCmd.Parameters.AddWithValue("@AllocatedCapital", seedAllocatedCapital);
                insertCmd.Parameters.AddWithValue("@CompoundingEnabled", seedCompoundingEnabled ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@CurrentCapital", seedAllocatedCapital);
                insertCmd.Parameters.AddWithValue("@Status", StrategyStatus.Running.ToString());
                insertCmd.Parameters.AddWithValue("@CreatedUtc", now.ToString("O"));
                insertCmd.Parameters.AddWithValue("@UpdatedUtc", now.ToString("O"));

                var newId = (long)(await insertCmd.ExecuteScalarAsync(ct))!;
                return new StrategyInstanceRecord
                {
                    Id = (int)newId,
                    StrategyType = strategyType,
                    Symbol = symbol,
                    AllocatedCapital = seedAllocatedCapital,
                    CompoundingEnabled = seedCompoundingEnabled,
                    CurrentCapital = seedAllocatedCapital,
                    Status = StrategyStatus.Running,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
            }
        }

        public async Task<StrategyInstanceRecord?> GetAsync(int id, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, StrategyType, Symbol, AllocatedCapital, CompoundingEnabled, CurrentCapital, Status, CreatedUtc, UpdatedUtc FROM StrategyInstances WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return ReadRecord(reader);
        }

        public async Task UpdateStatusAsync(int id, StrategyStatus status, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE StrategyInstances SET Status = @Status, UpdatedUtc = @UpdatedUtc WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Status", status.ToString());
            cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpdateCurrentCapitalAsync(int id, decimal newCurrentCapital, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE StrategyInstances SET CurrentCapital = @CurrentCapital, UpdatedUtc = @UpdatedUtc WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@CurrentCapital", newCurrentCapital);
            cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpdateAllocatedCapitalAsync(int id, decimal newAllocatedCapital, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE StrategyInstances SET AllocatedCapital = @AllocatedCapital, UpdatedUtc = @UpdatedUtc WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@AllocatedCapital", newAllocatedCapital);
            cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<StrategyInstanceRecord?> SelectByTypeAndSymbolAsync(SqliteConnection conn, string strategyType, string symbol, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, StrategyType, Symbol, AllocatedCapital, CompoundingEnabled, CurrentCapital, Status, CreatedUtc, UpdatedUtc FROM StrategyInstances WHERE StrategyType = @StrategyType AND Symbol = @Symbol;";
            cmd.Parameters.AddWithValue("@StrategyType", strategyType);
            cmd.Parameters.AddWithValue("@Symbol", symbol);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return ReadRecord(reader);
        }

        private static StrategyInstanceRecord ReadRecord(SqliteDataReader reader)
        {
            return new StrategyInstanceRecord
            {
                Id = reader.GetInt32(0),
                StrategyType = reader.GetString(1),
                Symbol = reader.GetString(2),
                AllocatedCapital = reader.GetDecimal(3),
                CompoundingEnabled = reader.GetInt32(4) != 0,
                CurrentCapital = reader.GetDecimal(5),
                Status = Enum.Parse<StrategyStatus>(reader.GetString(6)),
                CreatedUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
                UpdatedUtc = DateTime.Parse(reader.GetString(8)).ToUniversalTime()
            };
        }
    }
}
