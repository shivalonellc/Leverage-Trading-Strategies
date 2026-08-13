using LeverageTradingStrategies.Domain.TqqqAgent;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public class SqliteTqqqAgentDecisionRepository : ITqqqAgentDecisionRepository
    {
        private readonly ISqliteConnectionFactory _connectionFactory;

        public SqliteTqqqAgentDecisionRepository(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<long> InsertAsync(TqqqAgentDecisionRecord record, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO TqqqAgentDecisions
                    (CycleUtc, PortfolioSnapshotJson, MarketSnapshotJson, RawAction, RawConfidence, RawWhy,
                     Approved, FinalAction, Shares, RejectReason, OrderStatus, BrokerOrderId, FillPrice, RealizedPnL, ErrorMessage)
                VALUES
                    (@CycleUtc, @PortfolioSnapshotJson, @MarketSnapshotJson, @RawAction, @RawConfidence, @RawWhy,
                     @Approved, @FinalAction, @Shares, @RejectReason, @OrderStatus, @BrokerOrderId, @FillPrice, @RealizedPnL, @ErrorMessage);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@CycleUtc", record.CycleUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@PortfolioSnapshotJson", record.PortfolioSnapshotJson);
            cmd.Parameters.AddWithValue("@MarketSnapshotJson", record.MarketSnapshotJson);
            cmd.Parameters.AddWithValue("@RawAction", record.RawAction.ToString());
            cmd.Parameters.AddWithValue("@RawConfidence", record.RawConfidence);
            cmd.Parameters.AddWithValue("@RawWhy", record.RawWhy);
            cmd.Parameters.AddWithValue("@Approved", record.Approved ? 1 : 0);
            cmd.Parameters.AddWithValue("@FinalAction", record.FinalAction.ToString());
            cmd.Parameters.AddWithValue("@Shares", record.Shares);
            cmd.Parameters.AddWithValue("@RejectReason", (object?)record.RejectReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OrderStatus", record.OrderStatus);
            cmd.Parameters.AddWithValue("@BrokerOrderId", (object?)record.BrokerOrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FillPrice", (object?)record.FillPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RealizedPnL", (object?)record.RealizedPnL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)record.ErrorMessage ?? DBNull.Value);

            var newId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            return newId;
        }

        public async Task UpdateOrderResultAsync(
            long id,
            string orderStatus,
            string? brokerOrderId,
            decimal? fillPrice,
            decimal? realizedPnL,
            string? errorMessage,
            CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE TqqqAgentDecisions
                SET OrderStatus = @OrderStatus, BrokerOrderId = @BrokerOrderId,
                    FillPrice = @FillPrice, RealizedPnL = @RealizedPnL, ErrorMessage = @ErrorMessage
                WHERE Id = @Id;
                """;
            cmd.Parameters.AddWithValue("@OrderStatus", orderStatus);
            cmd.Parameters.AddWithValue("@BrokerOrderId", (object?)brokerOrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FillPrice", (object?)fillPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RealizedPnL", (object?)realizedPnL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<List<TqqqAgentDecisionRecord>> GetRecentAsync(int limit, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, CycleUtc, PortfolioSnapshotJson, MarketSnapshotJson, RawAction, RawConfidence, RawWhy,
                       Approved, FinalAction, Shares, RejectReason, OrderStatus, BrokerOrderId, FillPrice, RealizedPnL, ErrorMessage
                FROM TqqqAgentDecisions
                ORDER BY CycleUtc DESC
                LIMIT @Limit;
                """;
            cmd.Parameters.AddWithValue("@Limit", limit);
            return await ReadAllAsync(cmd, ct);
        }

        public async Task<List<TqqqAgentDecisionRecord>> GetByCycleRangeAsync(DateTime startUtcInclusive, DateTime endUtcExclusive, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, CycleUtc, PortfolioSnapshotJson, MarketSnapshotJson, RawAction, RawConfidence, RawWhy,
                       Approved, FinalAction, Shares, RejectReason, OrderStatus, BrokerOrderId, FillPrice, RealizedPnL, ErrorMessage
                FROM TqqqAgentDecisions
                WHERE CycleUtc >= @StartUtc AND CycleUtc < @EndUtc
                ORDER BY CycleUtc ASC;
                """;
            cmd.Parameters.AddWithValue("@StartUtc", startUtcInclusive.ToString("O"));
            cmd.Parameters.AddWithValue("@EndUtc", endUtcExclusive.ToString("O"));
            return await ReadAllAsync(cmd, ct);
        }

        private static async Task<List<TqqqAgentDecisionRecord>> ReadAllAsync(Microsoft.Data.Sqlite.SqliteCommand cmd, CancellationToken ct)
        {
            var results = new List<TqqqAgentDecisionRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new TqqqAgentDecisionRecord
                {
                    Id = reader.GetInt64(0),
                    CycleUtc = DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                    PortfolioSnapshotJson = reader.GetString(2),
                    MarketSnapshotJson = reader.GetString(3),
                    RawAction = Enum.Parse<TqqqAgentAction>(reader.GetString(4)),
                    RawConfidence = reader.GetDouble(5),
                    RawWhy = reader.GetString(6),
                    Approved = reader.GetInt32(7) != 0,
                    FinalAction = Enum.Parse<TqqqAgentAction>(reader.GetString(8)),
                    Shares = reader.GetInt32(9),
                    RejectReason = reader.IsDBNull(10) ? null : reader.GetString(10),
                    OrderStatus = reader.GetString(11),
                    BrokerOrderId = reader.IsDBNull(12) ? null : reader.GetString(12),
                    FillPrice = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                    RealizedPnL = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                    ErrorMessage = reader.IsDBNull(15) ? null : reader.GetString(15)
                });
            }
            return results;
        }
    }
}
