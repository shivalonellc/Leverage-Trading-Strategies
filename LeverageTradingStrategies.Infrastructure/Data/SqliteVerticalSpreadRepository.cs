using LeverageTradingStrategies.Infrastructure.Data.Entities;
using LeverageTradingStrategies.Infrastructure.Options;
using Microsoft.Data.Sqlite;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public class SqliteVerticalSpreadRepository : IVerticalSpreadRepository
    {
        private readonly ISqliteConnectionFactory _connectionFactory;

        public SqliteVerticalSpreadRepository(ISqliteConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<long> InsertAsync(VerticalSpreadStrategyRecord s, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO VerticalSpreadStrategies
                    (Symbol, SpreadType, OptionRight, ExpirationDate, ShortStrike, LongStrike,
                     ShortOptionSymbol, LongOptionSymbol, Contracts, ShortDeltaAtBuild, LongDeltaAtBuild,
                     NetCreditAtBuild, MaxRiskPerSpread, Status, NetCreditReceived, OpenedUtc, CreatedUtc, UpdatedUtc)
                VALUES
                    (@Symbol, @SpreadType, @OptionRight, @ExpirationDate, @ShortStrike, @LongStrike,
                     @ShortOptionSymbol, @LongOptionSymbol, @Contracts, @ShortDeltaAtBuild, @LongDeltaAtBuild,
                     @NetCreditAtBuild, @MaxRiskPerSpread, @Status, @NetCreditReceived, @OpenedUtc, @CreatedUtc, @UpdatedUtc);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@Symbol", s.Symbol);
            cmd.Parameters.AddWithValue("@SpreadType", s.SpreadType.ToString());
            cmd.Parameters.AddWithValue("@OptionRight", s.Right.ToString());
            cmd.Parameters.AddWithValue("@ExpirationDate", s.ExpirationDate.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@ShortStrike", s.ShortStrike);
            cmd.Parameters.AddWithValue("@LongStrike", s.LongStrike);
            cmd.Parameters.AddWithValue("@ShortOptionSymbol", s.ShortOptionSymbol);
            cmd.Parameters.AddWithValue("@LongOptionSymbol", s.LongOptionSymbol);
            cmd.Parameters.AddWithValue("@Contracts", s.Contracts);
            cmd.Parameters.AddWithValue("@ShortDeltaAtBuild", (object?)s.ShortDeltaAtBuild ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LongDeltaAtBuild", (object?)s.LongDeltaAtBuild ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NetCreditAtBuild", s.NetCreditAtBuild);
            cmd.Parameters.AddWithValue("@MaxRiskPerSpread", s.MaxRiskPerSpread);
            cmd.Parameters.AddWithValue("@Status", s.Status.ToString());
            cmd.Parameters.AddWithValue("@NetCreditReceived", (object?)s.NetCreditReceived ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OpenedUtc", s.OpenedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@CreatedUtc", s.CreatedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@UpdatedUtc", s.UpdatedUtc.ToString("O"));

            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        private const string SelectStrategyColumns = """
            SELECT Id, Symbol, SpreadType, OptionRight, ExpirationDate, ShortStrike, LongStrike,
                   ShortOptionSymbol, LongOptionSymbol, Contracts, ShortDeltaAtBuild, LongDeltaAtBuild,
                   NetCreditAtBuild, MaxRiskPerSpread, Status, NetCreditReceived, OpenedUtc, DeployedUtc,
                   ClosedUtc, RealizedPnL, CloseReason, CreatedUtc, UpdatedUtc
            FROM VerticalSpreadStrategies
            """;

        private static VerticalSpreadStrategyRecord ReadStrategy(SqliteDataReader r) => new()
        {
            Id = r.GetInt64(0),
            Symbol = r.GetString(1),
            SpreadType = Enum.Parse<VerticalSpreadType>(r.GetString(2)),
            Right = Enum.Parse<OptionRight>(r.GetString(3)),
            ExpirationDate = DateTime.Parse(r.GetString(4)),
            ShortStrike = r.GetDecimal(5),
            LongStrike = r.GetDecimal(6),
            ShortOptionSymbol = r.GetString(7),
            LongOptionSymbol = r.GetString(8),
            Contracts = r.GetInt32(9),
            ShortDeltaAtBuild = r.IsDBNull(10) ? null : r.GetDecimal(10),
            LongDeltaAtBuild = r.IsDBNull(11) ? null : r.GetDecimal(11),
            NetCreditAtBuild = r.GetDecimal(12),
            MaxRiskPerSpread = r.GetDecimal(13),
            Status = Enum.Parse<VerticalSpreadStatus>(r.GetString(14)),
            NetCreditReceived = r.IsDBNull(15) ? null : r.GetDecimal(15),
            OpenedUtc = DateTime.Parse(r.GetString(16)).ToUniversalTime(),
            DeployedUtc = r.IsDBNull(17) ? null : DateTime.Parse(r.GetString(17)).ToUniversalTime(),
            ClosedUtc = r.IsDBNull(18) ? null : DateTime.Parse(r.GetString(18)).ToUniversalTime(),
            RealizedPnL = r.IsDBNull(19) ? null : r.GetDecimal(19),
            CloseReason = r.IsDBNull(20) ? null : r.GetString(20),
            CreatedUtc = DateTime.Parse(r.GetString(21)).ToUniversalTime(),
            UpdatedUtc = DateTime.Parse(r.GetString(22)).ToUniversalTime()
        };

        public async Task<VerticalSpreadStrategyRecord?> GetByIdAsync(long id, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectStrategyColumns + " WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadStrategy(reader) : null;
        }

        public async Task<List<VerticalSpreadStrategyRecord>> GetAllAsync(CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectStrategyColumns + " ORDER BY CreatedUtc DESC;";
            var results = new List<VerticalSpreadStrategyRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(ReadStrategy(reader));
            return results;
        }

        public async Task<List<VerticalSpreadStrategyRecord>> GetActiveAsync(CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectStrategyColumns + " WHERE Status IN ('Paper', 'Live') ORDER BY CreatedUtc;";
            var results = new List<VerticalSpreadStrategyRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(ReadStrategy(reader));
            return results;
        }

        public async Task MarkDeployedAsync(long id, decimal netCreditReceived, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE VerticalSpreadStrategies
                SET Status = 'Live', NetCreditReceived = @NetCreditReceived, DeployedUtc = @Now, UpdatedUtc = @Now
                WHERE Id = @Id;
                """;
            cmd.Parameters.AddWithValue("@NetCreditReceived", netCreditReceived);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task MarkFailedAsync(long id, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            // Deliberately does NOT touch Status away from Paper -- a rejected Deploy attempt
            // should stay retryable, not get stuck in a dead-end "Failed" state that blocks
            // Save/Deploy from ever being tried again. Failed here just means "the LAST attempt
            // failed"; the order row itself carries the Rejected/Failed detail.
            cmd.CommandText = "UPDATE VerticalSpreadStrategies SET UpdatedUtc = @Now WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task MarkClosedAsync(long id, decimal realizedPnL, string closeReason, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE VerticalSpreadStrategies
                SET Status = 'Closed', RealizedPnL = @RealizedPnL, CloseReason = @CloseReason, ClosedUtc = @Now, UpdatedUtc = @Now
                WHERE Id = @Id;
                """;
            cmd.Parameters.AddWithValue("@RealizedPnL", realizedPnL);
            cmd.Parameters.AddWithValue("@CloseReason", closeReason);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<long> InsertOrderAsync(VerticalSpreadOrderRecord o, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO VerticalSpreadOrders
                    (VerticalSpreadStrategyId, ActionType, LongOptionSymbol, ShortOptionSymbol, Contracts,
                     RequestedPrice, Status, SubmittedUtc)
                VALUES
                    (@StrategyId, @ActionType, @LongOptionSymbol, @ShortOptionSymbol, @Contracts,
                     @RequestedPrice, @Status, @SubmittedUtc);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@StrategyId", o.VerticalSpreadStrategyId);
            cmd.Parameters.AddWithValue("@ActionType", o.ActionType.ToString());
            cmd.Parameters.AddWithValue("@LongOptionSymbol", o.LongOptionSymbol);
            cmd.Parameters.AddWithValue("@ShortOptionSymbol", o.ShortOptionSymbol);
            cmd.Parameters.AddWithValue("@Contracts", o.Contracts);
            cmd.Parameters.AddWithValue("@RequestedPrice", o.RequestedPrice);
            cmd.Parameters.AddWithValue("@Status", VerticalSpreadOrderStatus.Submitted.ToString());
            cmd.Parameters.AddWithValue("@SubmittedUtc", o.SubmittedUtc.ToString("O"));
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task MarkOrderFilledAsync(long orderId, decimal fillPrice, string? brokerOrderId, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE VerticalSpreadOrders
                SET Status = 'Filled', FillPrice = @FillPrice, BrokerOrderId = @BrokerOrderId, FilledUtc = @Now
                WHERE Id = @Id;
                """;
            cmd.Parameters.AddWithValue("@FillPrice", fillPrice);
            cmd.Parameters.AddWithValue("@BrokerOrderId", (object?)brokerOrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", orderId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public Task MarkOrderRejectedAsync(long orderId, string errorMessage, CancellationToken ct = default) =>
            SetOrderTerminalStatus(orderId, VerticalSpreadOrderStatus.Rejected, errorMessage, ct);

        public Task MarkOrderFailedAsync(long orderId, string errorMessage, CancellationToken ct = default) =>
            SetOrderTerminalStatus(orderId, VerticalSpreadOrderStatus.Failed, errorMessage, ct);

        private async Task SetOrderTerminalStatus(long orderId, VerticalSpreadOrderStatus status, string errorMessage, CancellationToken ct)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE VerticalSpreadOrders SET Status = @Status, ErrorMessage = @ErrorMessage WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Status", status.ToString());
            cmd.Parameters.AddWithValue("@ErrorMessage", errorMessage);
            cmd.Parameters.AddWithValue("@Id", orderId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<List<VerticalSpreadOrderRecord>> GetOrdersAsync(long strategyId, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, VerticalSpreadStrategyId, ActionType, LongOptionSymbol, ShortOptionSymbol, Contracts,
                       RequestedPrice, FillPrice, Status, BrokerOrderId, ErrorMessage, SubmittedUtc, FilledUtc
                FROM VerticalSpreadOrders
                WHERE VerticalSpreadStrategyId = @StrategyId
                ORDER BY SubmittedUtc DESC;
                """;
            cmd.Parameters.AddWithValue("@StrategyId", strategyId);
            var results = new List<VerticalSpreadOrderRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new VerticalSpreadOrderRecord
                {
                    Id = reader.GetInt64(0),
                    VerticalSpreadStrategyId = reader.GetInt64(1),
                    ActionType = Enum.Parse<VerticalSpreadOrderAction>(reader.GetString(2)),
                    LongOptionSymbol = reader.GetString(3),
                    ShortOptionSymbol = reader.GetString(4),
                    Contracts = reader.GetInt32(5),
                    RequestedPrice = reader.GetDecimal(6),
                    FillPrice = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    Status = Enum.Parse<VerticalSpreadOrderStatus>(reader.GetString(8)),
                    BrokerOrderId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
                    SubmittedUtc = DateTime.Parse(reader.GetString(11)).ToUniversalTime(),
                    FilledUtc = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)).ToUniversalTime()
                });
            }
            return results;
        }

        public async Task InsertMarkAsync(VerticalSpreadMarkRecord m, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO VerticalSpreadMarks
                    (VerticalSpreadStrategyId, MarkUtc, UnderlyingPrice, ShortMid, LongMid, SpreadMarkPrice,
                     UnrealizedPnL, ShortDelta, LongDelta, NetDelta, DaysToExpiration)
                VALUES
                    (@StrategyId, @MarkUtc, @UnderlyingPrice, @ShortMid, @LongMid, @SpreadMarkPrice,
                     @UnrealizedPnL, @ShortDelta, @LongDelta, @NetDelta, @DaysToExpiration);
                """;
            cmd.Parameters.AddWithValue("@StrategyId", m.VerticalSpreadStrategyId);
            cmd.Parameters.AddWithValue("@MarkUtc", m.MarkUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@UnderlyingPrice", m.UnderlyingPrice);
            cmd.Parameters.AddWithValue("@ShortMid", m.ShortMid);
            cmd.Parameters.AddWithValue("@LongMid", m.LongMid);
            cmd.Parameters.AddWithValue("@SpreadMarkPrice", m.SpreadMarkPrice);
            cmd.Parameters.AddWithValue("@UnrealizedPnL", m.UnrealizedPnL);
            cmd.Parameters.AddWithValue("@ShortDelta", (object?)m.ShortDelta ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LongDelta", (object?)m.LongDelta ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NetDelta", (object?)m.NetDelta ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DaysToExpiration", m.DaysToExpiration);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<List<VerticalSpreadMarkRecord>> GetMarksAsync(long strategyId, int limit, CancellationToken ct = default)
        {
            using var conn = _connectionFactory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, VerticalSpreadStrategyId, MarkUtc, UnderlyingPrice, ShortMid, LongMid, SpreadMarkPrice,
                       UnrealizedPnL, ShortDelta, LongDelta, NetDelta, DaysToExpiration
                FROM VerticalSpreadMarks
                WHERE VerticalSpreadStrategyId = @StrategyId
                ORDER BY MarkUtc DESC
                LIMIT @Limit;
                """;
            cmd.Parameters.AddWithValue("@StrategyId", strategyId);
            cmd.Parameters.AddWithValue("@Limit", limit);
            var results = new List<VerticalSpreadMarkRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new VerticalSpreadMarkRecord
                {
                    Id = reader.GetInt64(0),
                    VerticalSpreadStrategyId = reader.GetInt64(1),
                    MarkUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                    UnderlyingPrice = reader.GetDecimal(3),
                    ShortMid = reader.GetDecimal(4),
                    LongMid = reader.GetDecimal(5),
                    SpreadMarkPrice = reader.GetDecimal(6),
                    UnrealizedPnL = reader.GetDecimal(7),
                    ShortDelta = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    LongDelta = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    NetDelta = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    DaysToExpiration = reader.GetInt32(11)
                });
            }
            results.Reverse(); // oldest-first, natural order for a time-series chart
            return results;
        }
    }
}
