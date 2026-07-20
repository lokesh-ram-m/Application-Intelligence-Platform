using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

namespace Aip.Infrastructure;

/// <summary>
/// The logging adapter — Serilog behind <c>Microsoft.Extensions.Logging</c>'s <see cref="ILogger{T}"/>
/// abstraction, so every service that takes an <c>ILogger&lt;T&gt;</c> constructor dependency (the
/// Analysis pipeline, the language engines, the repository source) never knows Serilog exists. Two
/// sinks: console, for a human watching a run happen locally, and a <c>Logs</c> table (auto-created on
/// first write) in its own database — deliberately <b>not</b> Run History's database, so the two can be
/// scaled, retained, and eventually pointed at Azure SQL independently of each other (see
/// <see cref="ResolveLoggingConnectionString"/>). Minimum level is Debug for the app's own code;
/// framework/library namespaces (Microsoft.*, System.*) are raised to Information so EF Core/ASP.NET
/// Core's own chatter doesn't drown out actual signal — except EF Core's per-query SQL command text,
/// raised further to Warning, since it's pure noise next to the structured Runs/RepositoryRuns tables.
/// </summary>
public static class LoggingModule
{
    public static IServiceCollection AddAipLogging(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        string sqlConnection = ResolveLoggingConnectionString(configuration);

        var columnOptions = new ColumnOptions();
        columnOptions.Store.Add(StandardColumn.LogEvent); // full structured properties, not just the rendered message

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Information)
            // EF Core logs the full text of every SQL command it runs at Information — genuinely useful
            // for deep DB debugging, but pure noise for "what did this run do," and Runs/RepositoryRuns
            // already capture the same facts in structured form. Warning+ still surfaces real problems
            // (concurrency conflicts, slow-query warnings, etc.) — this is the more specific override, so
            // it wins over the broader "Microsoft" one above for this one noisy category.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", applicationName)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.MSSqlServer(
                connectionString: sqlConnection,
                sinkOptions: new MSSqlServerSinkOptions { TableName = "Logs", AutoCreateSqlTable = true },
                columnOptions: columnOptions)
            .CreateLogger();

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        return services;
    }

    /// <summary>
    /// The Logs database's own connection string — resolved independently of Run History's
    /// (<see cref="InfrastructureModule.ResolveSqlConnectionString"/>), never falling back to it, so the
    /// two genuinely can't drift into silently sharing a database by accident. Set Logging:ConnectionString
    /// (appsettings) or AIP_LOGGING_SQL_CONNECTION_STRING (env var, always wins).
    /// </summary>
    public static string ResolveLoggingConnectionString(IConfiguration configuration)
    {
        string? loggingConnection = Environment.GetEnvironmentVariable("AIP_LOGGING_SQL_CONNECTION_STRING") ?? configuration["Logging:ConnectionString"];
        if (string.IsNullOrWhiteSpace(loggingConnection))
            throw new InvalidOperationException(
                "No SQL connection string configured for logging. Set Logging:ConnectionString " +
                "(appsettings.Development.json) or AIP_LOGGING_SQL_CONNECTION_STRING (env var) to a SQL " +
                "Server / Azure SQL connection string. See README.md for the local Docker SQL Server setup.");

        return loggingConnection;
    }
}
