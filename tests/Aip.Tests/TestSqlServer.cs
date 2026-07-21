using Microsoft.Data.SqlClient;

namespace Aip.Tests;

/// <summary>
/// The SQL Server every SQL-backed test connects to — resolved from the environment, exactly like the
/// application itself resolves it in production (see
/// Aip.Infrastructure.InfrastructureModule.ResolveSqlConnectionString): no hardcoded server, credentials,
/// or "localhost" assumption baked into test source. That keeps these tests loosely coupled to wherever
/// they actually run — a developer's local Docker SQL Server, a CI-provisioned container, or a real Azure
/// SQL logical server in a pipeline — rather than only ever passing on one specific machine.
///
/// Each test gets its own throwaway, randomly-named database (via <see cref="NewConnectionString"/>) on
/// that same server, dropped again in the test's own <c>finally</c> block via
/// <see cref="DropDatabaseAsync"/>, so tests never collide with each other or leave stale state behind.
/// </summary>
internal static class TestSqlServer
{
    // AIP_TEST_SQL_SERVER is checked first (a server this test run is explicitly allowed to create/drop
    // throwaway databases against — set this in CI/Azure). AIP_SQL_CONNECTION_STRING is the fallback, since
    // most local dev setups already export one for the app itself and its "Database=" segment is simply
    // replaced with a fresh, random one per test.
    private static string BaseConnectionString =>
        StripDatabase(Environment.GetEnvironmentVariable("AIP_TEST_SQL_SERVER"))
        ?? StripDatabase(Environment.GetEnvironmentVariable("AIP_SQL_CONNECTION_STRING"))
        ?? throw new InvalidOperationException(
            "No test SQL Server configured. Set AIP_TEST_SQL_SERVER (or AIP_SQL_CONNECTION_STRING) to a " +
            "SQL Server / Azure SQL connection string this test run may create and drop throwaway " +
            "databases against. For local Docker SQL Server: " +
            "'Server=localhost,1433;User Id=sa;Password=<your-password>;TrustServerCertificate=True;' " +
            "— see README.md for the local Docker setup, or point this at an Azure SQL server in CI.");

    public static string NewConnectionString(out string databaseName)
    {
        databaseName = "AipTest_" + Guid.NewGuid().ToString("N");

        return $"{BaseConnectionString}Database={databaseName};";
    }

    public static async Task DropDatabaseAsync(string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection($"{BaseConnectionString}Database=master;");
        await master.OpenAsync();
        await using var cmd = new SqlCommand(
            $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END", master);
        await cmd.ExecuteNonQueryAsync();
    }

    // Both env vars are a full connection string with whatever Database= they were configured with (or
    // none) — strip it so every test can append its own fresh, random database name instead.
    private static string? StripDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "" };
        string result = builder.ConnectionString;

        return result.EndsWith(';') ? result : result + ";";
    }
}
