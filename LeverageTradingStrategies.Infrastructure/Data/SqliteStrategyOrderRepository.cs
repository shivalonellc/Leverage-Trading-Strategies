using LeverageTradingStrategies.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public class SqliteStrategyOrderRepository : IStrategyOrderRepository
    {
        private readonly ISqliteConnectionFactory _connectionFactory;

        public SqliteStrategyOrderRepository(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<long> InsertSubmittedAsync(StrategyOrderRecord order, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO StrategyOrders
                    (StrategyInstanceId, Symbol, ActionType, Side, Quantity, Reason, Status, IsSimulated, RequestedPrice, SubmittedUtc)
                VALUES
                    (@StrategyInstanceId, @Symbol, @ActionType, @Side, @Quantity, @Reason, @Status, @IsSimulated, @RequestedPrice, @SubmittedUtc);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@StrategyInstanceId", order.StrategyInstanceId);
            cmd.Parameters.AddWithValue("@Symbol", order.Symbol);
            cmd.Parameters.AddWithValue("@ActionType", order.ActionType);
            cmd.Parameters.AddWithValue("@Side", order.Side.ToString());
            cmd.Parameters.AddWithValue("@Quantity", order.Quantity);
            cmd.Parameters.AddWithValue("@Reason", order.Reason);
            cmd.Parameters.AddWithValue("@Status", StrategyOrderStatus.Submitted.ToString());
            cmd.Parameters.AddWithValue("@IsSimulated", order.IsSimulated ? 1 : 0);
            cmd.Parameters.AddWithValue("@RequestedPrice", (object?)order.RequestedPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SubmittedUtc", order.SubmittedUtc.ToString("O"));

            var newId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            return newId;
        }

        public async Task MarkFilledAsync(long orderId, decimal fillPrice, string? brokerOrderId, decimal? realizedPnL, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE StrategyOrders
                SET Status = @Status, FillPrice = @FillPrice, BrokerOrderId = @BrokerOrderId,
                    RealizedPnL = @RealizedPnL, FilledUtc = @FilledUtc
                WHERE Id = @Id;
                """;
            cmd.Parameters.AddWithValue("@Status", StrategyOrderStatus.Filled.ToString());
            cmd.Parameters.AddWithValue("@FillPrice", fillPrice);
            cmd.Parameters.AddWithValue("@BrokerOrderId", (object?)brokerOrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RealizedPnL", (object?)realizedPnL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FilledUtc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", orderId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task MarkFailedAsync(long orderId, string errorMessage, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE StrategyOrders SET Status = @Status, ErrorMessage = @ErrorMessage WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Status", StrategyOrderStatus.Failed.ToString());
            cmd.Parameters.AddWithValue("@ErrorMessage", errorMessage);
            cmd.Parameters.AddWithValue("@Id", orderId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<List<StrategyOrderRecord>> GetRecentAsync(int strategyInstanceId, int limit, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, StrategyInstanceId, Symbol, ActionType, Side, Quantity, Reason, Status, IsSimulated,
                       RequestedPrice, FillPrice, RealizedPnL, BrokerOrderId, ErrorMessage, SubmittedUtc, FilledUtc
                FROM StrategyOrders
                WHERE StrategyInstanceId = @StrategyInstanceId
                ORDER BY SubmittedUtc DESC
                LIMIT @Limit;
                """;
            cmd.Parameters.AddWithValue("@StrategyInstanceId", strategyInstanceId);
            cmd.Parameters.AddWithValue("@Limit", limit);

            var results = new List<StrategyOrderRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new StrategyOrderRecord
                {
                    Id = reader.GetInt64(0),
                    StrategyInstanceId = reader.GetInt32(1),
                    Symbol = reader.GetString(2),
                    ActionType = reader.GetString(3),
                    Side = Enum.Parse<StrategyOrderSide>(reader.GetString(4)),
                    Quantity = reader.GetInt32(5),
                    Reason = reader.GetString(6),
                    Status = Enum.Parse<StrategyOrderStatus>(reader.GetString(7)),
                    IsSimulated = reader.GetInt32(8) != 0,
                    RequestedPrice = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    FillPrice = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    RealizedPnL = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                    BrokerOrderId = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ErrorMessage = reader.IsDBNull(13) ? null : reader.GetString(13),
                    SubmittedUtc = DateTime.Parse(reader.GetString(14)).ToUniversalTime(),
                    FilledUtc = reader.IsDBNull(15) ? null : DateTime.Parse(reader.GetString(15)).ToUniversalTime()
                });
            }
            return results;
        }
    }
}
