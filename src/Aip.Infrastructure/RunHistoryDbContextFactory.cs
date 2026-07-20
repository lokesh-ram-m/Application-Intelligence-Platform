using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aip.Infrastructure;

/// <summary>
/// Lets `dotnet ef migrations add` create a <see cref="RunHistoryDbContext"/> without needing the full
/// app's DI container or a --startup-project — the standard escape hatch for a DbContext that lives in a
/// class library. Only used at design time (never at runtime — see InfrastructureModule for the real
/// connection string resolution); targets the local Docker SQL Server container by default (see
/// README.md), overridable via AIP_SQL_CONNECTION_STRING for generating a migration against a different
/// target (e.g. right before a one-time Azure SQL cutover).
/// </summary>
internal sealed class RunHistoryDbContextFactory : IDesignTimeDbContextFactory<RunHistoryDbContext>
{
    private const string LocalDockerConnectionString =
        "Server=localhost,1433;Database=AipHistory;User Id=sa;Password=Aip_Local_Dev_2026!;TrustServerCertificate=True;";

    public RunHistoryDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("AIP_SQL_CONNECTION_STRING") ?? LocalDockerConnectionString;
        var optionsBuilder = new DbContextOptionsBuilder<RunHistoryDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new RunHistoryDbContext(optionsBuilder.Options);
    }
}
