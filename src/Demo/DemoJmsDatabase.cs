using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  DemoJmsDatabase
//
//  Demo mode's stand-in for JMS's own database: an in-memory SQLite database
//  holding the subset of JMS's tables the app queries (JmsSchema.sql), filled
//  with made-up data by DemoSeeder. DemoJmsHandler runs the app's real queries
//  against it (via MySqlToSqlite) and applies its writes to it, so every page
//  works and changes stick — until the app restarts, which starts over fresh.
//
//  One connection, one caller at a time: queries are tiny and SQLite in-memory
//  databases are per-connection anyway.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DemoJmsDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DemoJmsDatabase(int seed = DemoSeeder.DefaultSeed)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        MySqlToSqlite.RegisterFunctions(_connection, () => DateTime.Now);

        Execute(LoadSchema());
        new DemoSeeder(seed).Seed(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static string LoadSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkshopDashboard.Demo.JmsSchema.sql")
            ?? throw new InvalidOperationException("Demo JMS schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Runs one of the app's JMS queries (MySQL) and answers the way JMS's query page
    /// does: CSV with a header row, or an empty body for a statement that returns no rows.</summary>
    public async Task<string> QueryCsvAsync(string mySql)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = MySqlToSqlite.Translate(mySql);
            using var reader = cmd.ExecuteReader();
            if (reader.FieldCount == 0) return "";

            var sb = new StringBuilder();
            sb.AppendJoin(',', Enumerable.Range(0, reader.FieldCount).Select(i => Csv(reader.GetName(i)))).Append('\n');
            while (reader.Read())
                sb.AppendJoin(',', Enumerable.Range(0, reader.FieldCount).Select(i => Csv(Format(reader.GetValue(i))))).Append('\n');
            return sb.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Runs SQLite directly (DemoJmsHandler's writes), with named parameters.</summary>
    public async Task<long> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Format(object value) => value switch
    {
        DBNull => "",
        double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
