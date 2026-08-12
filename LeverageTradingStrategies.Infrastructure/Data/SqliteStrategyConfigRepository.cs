using Microsoft.Data.Sqlite;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public class SqliteStrategyConfigRepository : IStrategyConfigRepository
    {
        private readonly ISqliteConnectionFactory _connectionFactory;

        public SqliteStrategyConfigRepository(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<Dictionary<string, string>> GetAllAsync(int strategyInstanceId, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM StrategyConfig WHERE StrategyInstanceId = @Id;";
            cmd.Parameters.AddWithValue("@Id", strategyInstanceId);

            var result = new Dictionary<string, string>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
            return result;
        }

        public async Task SeedDefaultsAsync(int strategyInstanceId, IReadOnlyDictionary<string, string> defaults, CancellationToken ct = default)
        {
            if (defaults.Count == 0)
                return;

            using var conn = _connectionFactory.CreateOpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO StrategyConfig (StrategyInstanceId, Key, Value, UpdatedUtc)
                    VALUES (@Id, @Key, @Value, @UpdatedUtc);
                    """;
                var idParam = cmd.CreateParameter(); idParam.ParameterName = "@Id"; cmd.Parameters.Add(idParam);
                var keyParam = cmd.CreateParameter(); keyParam.ParameterName = "@Key"; cmd.Parameters.Add(keyParam);
                var valueParam = cmd.CreateParameter(); valueParam.ParameterName = "@Value"; cmd.Parameters.Add(valueParam);
                var updatedParam = cmd.CreateParameter(); updatedParam.ParameterName = "@UpdatedUtc"; cmd.Parameters.Add(updatedParam);

                var nowIso = DateTime.UtcNow.ToString("O");
                foreach (var (key, value) in defaults)
                {
                    idParam.Value = strategyInstanceId;
                    keyParam.Value = key;
                    valueParam.Value = value;
                    updatedParam.Value = nowIso;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            await tx.CommitAsync(ct);
        }

        public async Task SetAsync(int strategyInstanceId, string key, string value, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO StrategyConfig (StrategyInstanceId, Key, Value, UpdatedUtc)
                VALUES (@Id, @Key, @Value, @UpdatedUtc)
                ON CONFLICT (StrategyInstanceId, Key) DO UPDATE SET Value = excluded.Value, UpdatedUtc = excluded.UpdatedUtc;
                """;
            cmd.Parameters.AddWithValue("@Id", strategyInstanceId);
            cmd.Parameters.AddWithValue("@Key", key);
            cmd.Parameters.AddWithValue("@Value", value);
            cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
