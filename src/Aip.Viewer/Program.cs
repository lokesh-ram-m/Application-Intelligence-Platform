using System.Text.Json;

using Aip.Abstractions.Documents;
using Aip.Abstractions.History;
using Aip.Infrastructure;
using Aip.Infrastructure.AzureBlob;
using Aip.Viewer;

using Markdig;

using Serilog;
using Serilog.Extensions.Hosting;

// ==========================================================================
//  Application Intelligence Platform — Document Viewer
//
//  The reader half of the Creator/Viewer split. Reads documentation LIVE from IDocumentStore on every
//  request — nothing is cached or materialized to disk. The Creator (Aip.Host) writes to the store;
//  this app is the only thing that ever renders it for humans to read.
//
//  This file only wires up configuration, DI, and routes. Route-handler logic lives in
//  DocumentEndpoints.cs; all HTML/CSS/JS lives under Views/.
// ==========================================================================

var builder = WebApplication.CreateBuilder(args);

// Configuration: identical layering to the Creator (Aip.Host) — appsettings.json (solution root,
// committed) → appsettings.Development.json (solution root, gitignored) → environment variables
// (always wins). Both apps read the same files, so they always agree on where docs live.
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(SolutionPaths.FindSolutionRoot() ?? Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables();

// Serilog: console + the same SQL Server database Run History/Aip.Host use (a Logs table) — see
// Aip.Infrastructure/LoggingModule for the shared setup both entry points call. AddAipLogging assigns
// Log.Logger and wires it into Microsoft.Extensions.Logging (ILogger<T>) via AddSerilog — that alone
// covers Aip.Host, a plain console app with a bare ServiceCollection and no ASP.NET Core pipeline. The
// Viewer additionally needs UseSerilogRequestLogging() below, whose middleware is built by reflection
// over Serilog.Extensions.Hosting.DiagnosticContext's constructor (Serilog.ILogger, then exposed via
// IDiagnosticContext) — the exact three registrations Serilog.Extensions.Hosting's own IHostBuilder.
// UseSerilog() integration performs. Registered explicitly here (not in the shared module) since only
// the Viewer's HTTP pipeline needs them.
builder.Services.AddAipLogging(builder.Configuration, "Aip.Viewer");
builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton<DiagnosticContext>();
builder.Services.AddSingleton<IDiagnosticContext>(sp => sp.GetRequiredService<DiagnosticContext>());

// Document store selection — identical logic to Aip.Host/PlatformComposition.cs, so the Creator and
// Viewer always resolve to the same store without either one needing to know which is active.
builder.Services.AddSingleton<IDocumentStore, FileSystemDocumentStore>();
string? blobConnection = Environment.GetEnvironmentVariable("AIP_BLOB_CONNECTION_STRING") ?? builder.Configuration["Storage:ConnectionString"];
if (!string.IsNullOrWhiteSpace(blobConnection))
{
    string container = Environment.GetEnvironmentVariable("AIP_BLOB_CONTAINER") ?? builder.Configuration["Storage:Container"] ?? "documents";
    builder.Services.AddSingleton<IDocumentStore>(_ => new AzureBlobDocumentStore(blobConnection, container));
}

// "What Changed" support — reads the same SQL Server database the Creator (Aip.Host) already writes
// version-change records to. See InfrastructureModule.AddAipVersionChanges for why this is a narrower
// registration than the Creator's full AddAipInfrastructure.
builder.Services.AddAipVersionChanges(builder.Configuration);

builder.Services.AddSingleton(new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
builder.Services.AddSingleton(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

WebApplication app = builder.Build();
app.UseSerilogRequestLogging();

// Landing page — every application that currently has documentation, read from the shared index
// (the Creator updates this after each run; see ExecutionPipeline.UpdateApplicationsIndexAsync).
app.MapGet("/", (IDocumentStore store, JsonSerializerOptions jsonOptions) =>
    DocumentEndpoints.RenderLandingAsync(store, jsonOptions));

// Pinned to a specific version — the real render handler. ASP.NET Core's endpoint routing resolves this
// ahead of the unversioned catch-all below by segment specificity (literal "v" + int-constrained segment
// beats {*path}), regardless of which route is registered first.
// path is nullable: a catch-all route segment that matches nothing (e.g. /{app}/v3 with no trailing
// path) binds to a missing route value, and ASP.NET Core's minimal-API parameter binding treats a
// missing value for a non-nullable string as 400 Bad Request rather than an empty string — this is what
// was silently 400ing every /favicon.ico request (and any other bare single/double-segment URL) all along.
app.MapGet("/{application}/v{version:int}/{*path}", (string application, int version, string? path, IDocumentStore store, IVersionChangeStore changes, JsonSerializerOptions jsonOptions, MarkdownPipeline markdownPipeline) =>
    DocumentEndpoints.RenderPageAsync(store, changes, application, version, path ?? "", jsonOptions, markdownPipeline));

// "What changed" — a literal "changes" segment beats the {*path} catch-all above by the same
// specificity rule, so this always wins for that exact URL regardless of registration order.
app.MapGet("/{application}/v{version:int}/changes", (string application, int version, IVersionChangeStore changes, MarkdownPipeline markdownPipeline) =>
    DocumentEndpoints.RenderChangesAsync(application, version, changes, markdownPipeline));

// No version in the URL — resolve "latest" from the version index and redirect to the pinned URL.
// path is nullable for the same reason as above (a bare /{application} URL, or /favicon.ico, matches
// this route with the catch-all capturing nothing).
app.MapGet("/{application}/{*path}", (string application, string? path, IDocumentStore store, JsonSerializerOptions jsonOptions) =>
    DocumentEndpoints.RedirectToLatestAsync(application, path ?? "", store, jsonOptions));

try { app.Run(); }
finally { await Log.CloseAndFlushAsync(); }
