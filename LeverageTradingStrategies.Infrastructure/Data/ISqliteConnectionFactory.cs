using Microsoft.Data.Sqlite;

namespace LeverageTradingStrategies.Infrastructure.Data
{
    public interface ISqliteConnectionFactory
    {
        SqliteConnection CreateOpenConnection();
    }

    public class SqliteConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connectionString;

        public SqliteConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public SqliteConnection CreateOpenConnection()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
