namespace LeverageTradingStrategies.Infrastructure.Data
{
    public class SqliteTqqqAgentStateRepository : ITqqqAgentStateRepository
    {
        private readonly ISqliteConnectionFactory _connectionFactory;

        public SqliteTqqqAgentStateRepository(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<decimal?> GetDayStartEquityAsync(string tradeDateEt, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DayStartEquity FROM TqqqAgentDailyState WHERE TradeDateEt = @TradeDateEt;";
            cmd.Parameters.AddWithValue("@TradeDateEt", tradeDateEt);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToDecimal(result);
        }

        public async Task SetDayStartEquityIfAbsentAsync(string tradeDateEt, decimal equity, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO TqqqAgentDailyState (TradeDateEt, DayStartEquity, CreatedUtc)
                VALUES (@TradeDateEt, @DayStartEquity, @CreatedUtc);
                """;
            cmd.Parameters.AddWithValue("@TradeDateEt", tradeDateEt);
            cmd.Parameters.AddWithValue("@DayStartEquity", equity);
            cmd.Parameters.AddWithValue("@CreatedUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<TqqqAgentControlStateRow> GetControlStateAsync(CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT IsKilled, IsPaused, Reason, UpdatedUtc FROM TqqqAgentControlState WHERE Id = 1;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new TqqqAgentControlStateRow
                {
                    IsKilled = reader.GetInt32(0) != 0,
                    IsPaused = reader.GetInt32(1) != 0,
                    Reason = reader.IsDBNull(2) ? null : reader.GetString(2),
                    UpdatedUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime()
                };
            }

            // Row is seeded by the schema migration itself (INSERT OR IGNORE Id=1), so this
            // branch should never actually run -- but fail safe (killed) rather than assume
            // trading is allowed if the seed row is somehow missing.
            return new TqqqAgentControlStateRow { IsKilled = true, IsPaused = false, Reason = "Control state row missing.", UpdatedUtc = DateTime.UtcNow };
        }

        public async Task SetKilledAsync(bool killed, string? reason, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE TqqqAgentControlState SET IsKilled = @IsKilled, Reason = @Reason, UpdatedUtc = @UpdatedUtc WHERE Id = 1;";
            cmd.Parameters.AddWithValue("@IsKilled", killed ? 1 : 0);
            cmd.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task SetPausedAsync(bool paused, string? reason, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE TqqqAgentControlState SET IsPaused = @IsPaused, Reason = @Reason, UpdatedUtc = @UpdatedUtc WHERE Id = 1;";
            cmd.Parameters.AddWithValue("@IsPaused", paused ? 1 : 0);
            cmd.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
