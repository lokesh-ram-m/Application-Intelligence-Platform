using Aip.Abstractions.Analysis;
using Aip.Abstractions.Plugins;
using Aip.Core.Domain;
using Aip.Engines.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aip.Plugins.AspNetCore;

/// <summary>
/// Detects cross-cutting infrastructure — caching, authentication schemes, CORS, health checks, logging,
/// messaging, background jobs, and the middleware pipeline — from the framework APIs an app calls
/// (<c>services.Add…()</c> registrations and <c>app.Use…()</c> middleware). These APIs are identical across
/// every ASP.NET Core app, so detection is generic and does not depend on repository-specific naming.
/// </summary>
internal sealed class InfrastructureAnalyzer : IAnalyzer
{
    public string Name => "infrastructure";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        foreach (SyntaxTree tree in model.Trees)
        {
            string path = model.PathOf(tree);
            foreach (InvocationExpressionSyntax inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax member) continue;
                string call = member.Name.Identifier.Text;
                (string Kind, string Name, string Detail)? fact = Classify(call);
                if (fact is null) continue;

                Evidence ev = context.Evidence(path, Sym.Line(inv), call);
                var props = new List<(string, string)> { ("name", fact.Value.Name) };
                if (fact.Value.Detail.Length > 0) props.Add(("detail", fact.Value.Detail));
                if (fact.Value.Kind == "Middleware") props.Add(("order", Sym.Line(inv).ToString()));
                if (StringArgOf(inv) is { Length: > 0 } arg) props.Add(("arg", arg));

                sink.Add(NodeDiscovery.Create(
                    context.AppNodeId(Sym.Seg(fact.Value.Kind.ToLowerInvariant(), fact.Value.Name)),
                    NodeKind.From(fact.Value.Kind), new[] { ev }, Confidence.From(0.9), Sym.Props(props.ToArray())));
            }
        }

        return Task.CompletedTask;
    }

    // Framework API method name → the architectural fact it implies. Generic across any .NET app.
    private static (string Kind, string Name, string Detail)? Classify(string call) => call switch
    {
        // Caching
        "AddMemoryCache" => ("Cache", "In-memory cache", "IMemoryCache"),
        "AddDistributedMemoryCache" => ("Cache", "Distributed cache (in-memory)", "IDistributedCache"),
        "AddStackExchangeRedisCache" or "AddRedis" => ("Cache", "Redis (distributed cache)", "StackExchange.Redis"),
        "AddResponseCaching" => ("Cache", "Response caching", ""),
        "AddOutputCache" => ("Cache", "Output caching", ""),

        // Authentication schemes
        "AddJwtBearer" => ("AuthScheme", "JWT Bearer", "jwt"),
        "AddCookie" => ("AuthScheme", "Cookie", "cookie"),
        "AddOpenIdConnect" => ("AuthScheme", "OpenID Connect", "oidc"),
        "AddMicrosoftIdentityWebApi" or "AddMicrosoftIdentityWebApp" => ("AuthScheme", "Microsoft Entra ID (Azure AD)", "azure-ad"),
        "AddNegotiate" => ("AuthScheme", "Windows / Negotiate", "negotiate"),
        "AddIdentityServerJwt" => ("AuthScheme", "IdentityServer JWT", "identityserver"),
        "AddOktaMvc" or "AddOktaWebApi" => ("AuthScheme", "Okta", "okta"),
        "AddAuth0WebAppAuthentication" or "AddAuth0Authentication" => ("AuthScheme", "Auth0", "auth0"),

        // CORS / authorization
        "AddCors" => ("Cors", "CORS configured", "policy"),
        "UseCors" => ("Cors", "CORS middleware", "middleware"),
        "AddAuthorization" => ("Authorization", "Authorization policies", ""),

        // Health checks
        "AddHealthChecks" => ("HealthCheck", "Health checks", ""),
        "MapHealthChecks" => ("HealthCheck", "Health check endpoint", "endpoint"),

        // Logging / audit
        "UseSerilog" => ("Logging", "Serilog", "structured logging"),
        "UseSerilogRequestLogging" => ("Logging", "Serilog request logging", "request logging"),
        "AddAuditLog" or "UseAuditMiddleware" => ("Logging", "Audit logging", "audit"),

        // Messaging
        "AddMassTransit" => ("Messaging", "MassTransit", "bus"),
        "AddCap" => ("Messaging", "CAP (event bus)", "bus"),
        "AddRabbitMQ" or "AddRabbitMq" => ("Messaging", "RabbitMQ", "broker"),
        "AddServiceBus" or "AddAzureServiceBus" => ("Messaging", "Azure Service Bus", "broker"),

        // Background jobs
        "AddHangfire" => ("BackgroundJob", "Hangfire", "jobs"),
        "AddQuartz" => ("BackgroundJob", "Quartz.NET", "scheduler"),

        // Resilience (Polly)
        "AddPolicyHandler" or "AddResilienceHandler" or "AddStandardResilienceHandler"
                      => ("Resilience", "Polly resilience", "retry / circuit-breaker"),

        // Middleware pipeline (ordered)
        "UseAuthentication" => ("Middleware", "Authentication", ""),
        "UseAuthorization" => ("Middleware", "Authorization", ""),
        "UseExceptionHandler" or "UseAuditMiddlewareGlobal" => ("Middleware", "Global exception handling", ""),
        "UseRateLimiter" => ("Middleware", "Rate limiting", ""),
        "UseResponseCompression" => ("Middleware", "Response compression", ""),
        "UseHttpsRedirection" => ("Middleware", "HTTPS redirection", ""),

        _ => null,
    };

    private static string? StringArgOf(InvocationExpressionSyntax inv) =>
        (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
}

/// <summary>
/// Detects CQRS <b>semantically</b> via MediatR contracts — not by name. A type implementing
/// <c>IRequest</c>/<c>IRequest&lt;T&gt;</c> is a request; <c>IRequestHandler&lt;TReq,TRes&gt;</c> is its
/// handler; <c>INotification</c>/<c>INotificationHandler</c> are events. Command-vs-query is classified by
/// the strongest available structural signal (namespace/folder, then result type), never name alone.
/// </summary>
internal sealed class CqrsAnalyzer : IAnalyzer
{
    public string Name => "cqrs";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract) continue;
            Evidence ev = context.Evidence(path, Sym.Line(decl), Sym.Name(symbol));

            KnowledgeIdentity Id(ITypeSymbol s) => context.NodeId(Sym.Seg("project", Sym.ProjectOf(s) ?? project), Sym.Seg("type", Sym.Name(s)));

            // A request: IRequest or IRequest<T>.
            INamedTypeSymbol? request = symbol.AllInterfaces.FirstOrDefault(i => i.Name == "IRequest");
            if (request is not null)
            {
                ITypeSymbol? result = request.TypeArguments.Length == 1 ? request.TypeArguments[0] : null;
                string kind = ClassifyCqrs(symbol, result);
                var props = new List<(string, string)> { ("name", symbol.Name) };
                if (result is not null && result.Name != "Unit") props.Add(("returns", result.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                sink.Add(NodeDiscovery.Create(Id(symbol), NodeKind.From(kind), new[] { ev }, Confidence.From(0.85), Sym.Props(props.ToArray())));
            }

            // An event: INotification.
            if (symbol.AllInterfaces.Any(i => i.Name == "INotification"))
                sink.Add(NodeDiscovery.Create(Id(symbol), NodeKind.From("Event"), new[] { ev }, Confidence.From(0.85), Sym.Props(("name", symbol.Name))));

            // A handler: IRequestHandler<TReq,TRes> or INotificationHandler<T> — link handler → message.
            foreach (INamedTypeSymbol h in symbol.AllInterfaces.Where(i => i.Name is "IRequestHandler" or "INotificationHandler"))
            {
                if (h.TypeArguments.FirstOrDefault() is not INamedTypeSymbol msg || !msg.Locations.Any(l => l.IsInSource)) continue;
                sink.Add(NodeDiscovery.Create(Id(symbol), NodeKind.From("Handler"), new[] { ev }, Confidence.From(0.85), Sym.Props(("name", symbol.Name))));
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("HANDLES"), Id(symbol), Id(msg), new[] { ev }, Confidence.From(0.9)));
            }
        }

        return Task.CompletedTask;
    }

    // Command vs query — structural signals first (namespace/folder), then result type; name is last resort.
    private static string ClassifyCqrs(INamedTypeSymbol symbol, ITypeSymbol? result)
    {
        string ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns.Contains("Command", StringComparison.OrdinalIgnoreCase)) return "Command";
        if (ns.Contains("Quer", StringComparison.OrdinalIgnoreCase)) return "Query";
        if (symbol.Name.EndsWith("Command", StringComparison.Ordinal)) return "Command";
        if (symbol.Name.EndsWith("Query", StringComparison.Ordinal)) return "Query";
        // Structural fallback: no result (Unit/void) mutates → Command; returns data → Query.

        return result is null || result.Name == "Unit" ? "Command" : "Query";
    }
}

/// <summary>
/// Detects the messaging architecture <b>broker-agnostically</b>: who publishes, who consumes, and whether a
/// dead-letter queue is configured — the same way regardless of transport (RabbitMQ, Azure Service Bus, AWS
/// SQS/SNS, Kafka, MassTransit, NServiceBus, CAP). Each broker's publish/consume/DLQ APIs are recognized, but
/// they all emit one uniform model: <c>Type —PUBLISHES→ Message ←CONSUMES— Consumer</c>, a MessageBroker node,
/// and a DLQ flag. New brokers are added by extending the tables below — the output shape never changes.
/// </summary>
internal sealed class MessagingAnalyzer : IAnalyzer
{
    public string Name => "messaging-graph";

    // Broker SDK root namespace → friendly broker name. A file importing one of these "uses" that broker.
    private static readonly (string Ns, string Broker)[] Brokers =
    {
        ("Azure.Messaging.ServiceBus", "Azure Service Bus"),
        ("Amazon.SQS", "AWS SQS"),
        ("Amazon.SimpleNotificationService", "AWS SNS"),
        ("Confluent.Kafka", "Apache Kafka"),
        ("RabbitMQ", "RabbitMQ"),
        ("MassTransit", "MassTransit"),
        ("NServiceBus", "NServiceBus"),
        ("DotNetCore.CAP", "CAP"),
    };
    private static readonly HashSet<string> PublishMethods = new(StringComparer.Ordinal)
        { "BasicPublish", "Publish", "PublishAsync", "Send", "SendAsync", "SendMessageAsync", "SendMessagesAsync", "ProduceAsync", "Produce" };
    private static readonly HashSet<string> ConsumeMethods = new(StringComparer.Ordinal)
        { "Consume", "ReceiveMessageAsync", "ReceiveMessagesAsync", "StartProcessingAsync" };
    private static readonly string[] ConsumerInterfaces = { "IConsumer", "IHandleMessages", "IMessageHandler", "IIntegrationEventHandler", "ICapSubscribe" };
    private static readonly string[] DlqMarkers = { "x-dead-letter", "DeadLetter", "RedrivePolicy", "ConfigureDeadLetter", "dead-letter" };

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;
        KnowledgeIdentity TypeId(ITypeSymbol s) => context.NodeId(Sym.Seg("project", Sym.ProjectOf(s) ?? project), Sym.Seg("type", Sym.Name(s)));

        // Pass 1 — consumers declared via a consumer interface (semantic, transport-independent).
        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            INamedTypeSymbol? iface = symbol.AllInterfaces.FirstOrDefault(i => ConsumerInterfaces.Contains(i.Name));
            if (iface is null) continue;
            Evidence ev = context.Evidence(path, Sym.Line(decl), symbol.Name);
            sink.Add(NodeDiscovery.Create(TypeId(symbol), NodeKind.From("Consumer"), new[] { ev }, Confidence.From(0.9), Sym.Props(("name", symbol.Name))));
            if (iface.TypeArguments.FirstOrDefault() is INamedTypeSymbol msg && msg.Locations.Any(l => l.IsInSource))
            {
                sink.Add(NodeDiscovery.Create(TypeId(msg), NodeKind.From("Message"), new[] { ev }, Confidence.From(0.8), Sym.Props(("name", msg.Name))));
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("CONSUMES"), TypeId(symbol), TypeId(msg), new[] { ev }, Confidence.From(0.9)));
            }
        }

        // Pass 2 — publish/consume calls, broker detection and DLQ, only inside files that use a broker SDK
        // (gating on the SDK namespace keeps common verbs like Send/Publish from producing false positives).
        var brokersSeen = new HashSet<string>();
        bool dlq = false;
        foreach (SyntaxTree tree in model.Trees)
        {
            var usings = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name?.ToString() ?? "").ToList();
            var fileBrokers = Brokers.Where(b => usings.Any(u => u.StartsWith(b.Ns, StringComparison.Ordinal))).Select(b => b.Broker).ToList();
            if (fileBrokers.Count == 0) continue;
            foreach (string b in fileBrokers) brokersSeen.Add(b);

            string path = model.PathOf(tree);
            SemanticModel sm = model.GetSemanticModel(tree);
            if (!dlq && DlqMarkers.Any(m => tree.ToString().Contains(m, StringComparison.Ordinal))) dlq = true;

            foreach (InvocationExpressionSyntax inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? method = (inv.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
                if (method is null || (!PublishMethods.Contains(method) && !ConsumeMethods.Contains(method))) continue;

                INamedTypeSymbol? owner = inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } td
                    ? sm.GetDeclaredSymbol(td) as INamedTypeSymbol : null;
                if (owner is null) continue;
                Evidence ev = context.Evidence(path, Sym.Line(inv), method);

                if (PublishMethods.Contains(method))
                {
                    ITypeSymbol? msg = inv.ArgumentList.Arguments
                        .Select(a => sm.GetTypeInfo(a.Expression).Type)
                        .FirstOrDefault(t => t is INamedTypeSymbol { TypeKind: TypeKind.Class, SpecialType: SpecialType.None } n && n.Locations.Any(l => l.IsInSource));
                    if (msg is not null)
                    {
                        sink.Add(NodeDiscovery.Create(TypeId(msg), NodeKind.From("Message"), new[] { ev }, Confidence.From(0.7), Sym.Props(("name", msg.Name))));
                        sink.Add(RelationshipDiscovery.Create(RelationshipType.From("PUBLISHES"), TypeId(owner), TypeId(msg), new[] { ev }, Confidence.From(0.8)));
                    }
                }
                else
                {
                    sink.Add(NodeDiscovery.Create(TypeId(owner), NodeKind.From("Consumer"), new[] { ev }, Confidence.From(0.75), Sym.Props(("name", owner.Name))));
                }
            }
        }

        Evidence brokerEv = context.Evidence(context.Artifact.Path, null, "messaging");
        foreach (string broker in brokersSeen)
            sink.Add(NodeDiscovery.Create(context.AppNodeId(Sym.Seg("messagebroker", broker)), NodeKind.From("MessageBroker"),
                new[] { brokerEv }, Confidence.From(0.9), Sym.Props(("name", broker))));
        if (dlq)
            sink.Add(NodeDiscovery.Create(context.AppNodeId(Sym.Seg("messaging", "dlq")), NodeKind.From("Messaging"),
                new[] { brokerEv }, Confidence.From(0.85), Sym.Props(("name", "Dead-letter queue (DLQ)"))));

        return Task.CompletedTask;
    }
}

/// <summary>Detects FluentValidation validators (types extending <c>AbstractValidator&lt;T&gt;</c>) and links them to the type they validate.</summary>
internal sealed class ValidatorAnalyzer : IAnalyzer
{
    public string Name => "validators";

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class) continue;
            INamedTypeSymbol? baseValidator = Sym.BaseChain(symbol).FirstOrDefault(b => b.Name == "AbstractValidator");
            if (baseValidator is null) continue;

            Evidence ev = context.Evidence(path, Sym.Line(decl), symbol.Name);
            KnowledgeIdentity Id(ITypeSymbol s) => context.NodeId(Sym.Seg("project", Sym.ProjectOf(s) ?? project), Sym.Seg("type", Sym.Name(s)));

            sink.Add(NodeDiscovery.Create(Id(symbol), NodeKind.From("Validator"), new[] { ev }, Confidence.Full, Sym.Props(("name", symbol.Name))));

            if (baseValidator.TypeArguments.FirstOrDefault() is INamedTypeSymbol validated && validated.Locations.Any(l => l.IsInSource))
                sink.Add(RelationshipDiscovery.Create(RelationshipType.From("VALIDATES"), Id(symbol), Id(validated), new[] { ev }, Confidence.Full));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects the data-access approach for <b>any</b> provider — not just EF Core. Recognizes Dapper (its
/// namespace), raw ADO.NET connection types (SQL Server / PostgreSQL / MySQL / SQLite / Oracle), and the
/// MongoDB driver, and reports the approach + database. Detection keys on framework types, so it is generic.
/// </summary>
internal sealed class DataAccessAnalyzer : IAnalyzer
{
    public string Name => "data-access";

    // Type-name marker → (approach, database). Dapper is detected from its namespace instead.
    private static readonly (string Marker, string Approach, string Db)[] Signals =
    {
        ("SqlConnection",     "ADO.NET",        "SQL Server"),
        ("NpgsqlConnection",  "ADO.NET",        "PostgreSQL"),
        ("MySqlConnection",   "ADO.NET",        "MySQL"),
        ("SqliteConnection",  "ADO.NET",        "SQLite"),
        ("OracleConnection",  "ADO.NET",        "Oracle"),
        ("IMongoCollection",  "MongoDB driver", "MongoDB"),
        ("IMongoDatabase",    "MongoDB driver", "MongoDB"),
        ("MongoClient",       "MongoDB driver", "MongoDB"),
    };

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        var seen = new HashSet<string>();

        void Emit(string approach, string db, Evidence ev)
        {
            string name = db.Length > 0 ? $"{approach} ({db})" : approach;
            if (!seen.Add(name)) return;
            sink.Add(NodeDiscovery.Create(context.AppNodeId(Sym.Seg("dataaccess", name)), NodeKind.From("DataAccess"),
                new[] { ev }, Confidence.From(0.85), Sym.Props(("name", name), ("approach", approach), ("db", db))));
        }

        foreach (SyntaxTree tree in model.Trees)
        {
            var root = tree.GetRoot();
            Evidence ev = context.Evidence(model.PathOf(tree), null, "data-access");

            if (root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => (u.Name?.ToString() ?? "").StartsWith("Dapper", StringComparison.Ordinal)))
                Emit("Dapper (micro-ORM)", "", ev);

            var names = root.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.Text).ToHashSet(StringComparer.Ordinal);
            foreach ((string marker, string approach, string db) in Signals)
                if (names.Contains(marker)) Emit(approach, db, ev);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects <b>how</b> infrastructure SDKs are actually used — not just that a package is referenced, and
/// not just which method was called, but WHAT DOMAIN TYPE the call operates on. For every invocation,
/// resolves the REAL symbol via Roslyn's semantic model and checks which assembly actually declares it —
/// matched against the same <see cref="CapabilityRules"/> table already used to detect package references,
/// so every one of those ~50 technologies gets usage detection automatically, with no per-SDK method-name
/// list to hand-maintain. This depends on the analyzed repo's own NuGet package assemblies being loaded
/// into the compilation (see Aip.Engines.Roslyn's MSBuildWorkspace-backed loading) — without that, external
/// symbols don't resolve and this analyzer naturally finds nothing, rather than guessing from syntax.
///
/// The domain-type linkage (<see cref="InferSubject"/>) is what turns "Add, SaveChangesAsync" into
/// "Add(Contract), SaveChangesAsync" — grounded in the receiver's own generic type argument (e.g.
/// <c>DbSet&lt;Contract&gt;.Add(...)</c>) or, failing that, the first argument whose type carries real
/// domain meaning (e.g. <c>SendAsync(emailMessage)</c> → "EmailMessage"). Neither technique is specific to
/// EF Core, Polly, or any one SDK — it works for any generic-receiver or typed-argument call shape, so it
/// generalizes across every technology this analyzer tracks without per-technology special-casing.
/// </summary>
internal sealed class TechnologyUsageAnalyzer : IAnalyzer
{
    public string Name => "tech-usage";

    // Types that never carry domain meaning on their own — inferring a "subject" from one of these would
    // be noise, not insight (e.g. a call whose only argument is a CancellationToken tells us nothing about
    // what the call is really operating on).
    private static readonly HashSet<string> UninformativeTypes = new(StringComparer.Ordinal)
    {
        "String", "Int32", "Int64", "Int16", "Boolean", "Double", "Single", "Decimal", "Guid", "DateTime",
        "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Object", "Byte", "Byte[]", "Stream",
        "CancellationToken", "Void",
    };

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        var usage = new Dictionary<string, (string Category, Dictionary<string, SortedSet<string>> ByClass)>();
        Evidence ev = context.Evidence(context.Artifact.Path, null, "tech-usage");

        foreach (SyntaxTree tree in model.Trees)
        {
            var root = tree.GetRoot();
            var sm = model.GetSemanticModel(tree);

            foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax member) continue;

                string? assemblyName = sm.GetSymbolInfo(inv).Symbol?.ContainingAssembly?.Name;
                if (assemblyName is null) continue;   // unresolved (e.g. dynamic, or genuinely not from a known package)

                (string Fragment, string Category, string Name) match =
                    CapabilityRules.Rules.FirstOrDefault(r => assemblyName.Contains(r.Fragment, StringComparison.OrdinalIgnoreCase));
                if (match.Fragment is null) continue;   // no rule matches this assembly — not a tracked technology

                string tech = match.Name;
                string method = member.Name.Identifier.Text;
                string owner = inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } td
                    && sm.GetDeclaredSymbol(td) is INamedTypeSymbol o ? o.Name : "(module)";

                string? subject = InferSubject(sm, member, inv);
                string methodLabel = subject is null ? method : $"{method}({subject})";

                if (!usage.TryGetValue(tech, out var entry)) usage[tech] = entry = (match.Category, new Dictionary<string, SortedSet<string>>());
                if (!entry.ByClass.TryGetValue(owner, out var set)) entry.ByClass[owner] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(methodLabel);
            }
        }

        foreach ((string tech, (string category, Dictionary<string, SortedSet<string>> byClass)) in usage)
        {
            string usageStr = string.Join("; ", byClass.Take(6).Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value.Take(8))})"));
            sink.Add(NodeDiscovery.Create(context.AppNodeId(Sym.Seg("technology", tech)), NodeKind.From("Technology"),
                new[] { ev }, Confidence.From(0.85), Sym.Props(("name", tech), ("category", category), ("usage", usageStr))));
        }

        return Task.CompletedTask;
    }

    // Two generic, repo-agnostic techniques, tried in order:
    //  1. The receiver's own generic type argument — `_context.Contracts.Add(...)` where `Contracts` is
    //     typed `DbSet<Contract>`, or any IQueryable<T>/ICollection<T>/generic-repository-style receiver.
    //  2. The first argument whose own type carries domain meaning — `SendAsync(emailMessage)`.
    // Neither depends on knowing EF Core, Polly, or any specific SDK's shape in advance.
    private static string? InferSubject(SemanticModel sm, MemberAccessExpressionSyntax member, InvocationExpressionSyntax inv)
    {
        TypeInfo receiverType = sm.GetTypeInfo(member.Expression);
        if (receiverType.Type is INamedTypeSymbol { IsGenericType: true } namedReceiver)
        {
            string? fromReceiver = namedReceiver.TypeArguments
                .Select(t => t.Name)
                .FirstOrDefault(n => n.Length > 0 && !UninformativeTypes.Contains(n));
            if (fromReceiver is not null) return fromReceiver;
        }

        foreach (ArgumentSyntax arg in inv.ArgumentList.Arguments)
        {
            string? name = sm.GetTypeInfo(arg.Expression).Type?.Name;
            if (name is { Length: > 0 } && !UninformativeTypes.Contains(name)
                && !name.StartsWith("Func", StringComparison.Ordinal) && !name.StartsWith("Action", StringComparison.Ordinal))

                return name;
        }

        return null;
    }
}

/// <summary>Detects MVC filters by contract — authorization / action / exception / result / endpoint filters.</summary>
internal sealed class FilterAnalyzer : IAnalyzer
{
    public string Name => "filters";

    private static string? FilterKind(string iface) => iface switch
    {
        "IAuthorizationFilter" or "IAsyncAuthorizationFilter" => "authorization",
        "IActionFilter" or "IAsyncActionFilter" => "action",
        "IExceptionFilter" or "IAsyncExceptionFilter" => "exception",
        "IResultFilter" or "IAsyncResultFilter" => "result",
        "IEndpointFilter" => "endpoint",
        _ => null,
    };

    public Task AnalyzeAsync(IAnalysisContext context, IDiscoverySink sink, CancellationToken ct = default)
    {
        var model = (RoslynSemanticModel)context.Model;
        string project = context.Artifact.Name;

        foreach ((TypeDeclarationSyntax decl, INamedTypeSymbol symbol, _, string path) in Sym.Types(model))
        {
            if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract) continue;
            string? kind = symbol.AllInterfaces.Select(i => FilterKind(i.Name)).FirstOrDefault(k => k is not null);
            if (kind is null) continue;

            Evidence ev = context.Evidence(path, Sym.Line(decl), symbol.Name);
            sink.Add(NodeDiscovery.Create(
                context.NodeId(Sym.Seg("project", Sym.ProjectOf(symbol) ?? project), Sym.Seg("type", Sym.Name(symbol))),
                NodeKind.From("Filter"), new[] { ev }, Confidence.Full, Sym.Props(("name", symbol.Name), ("kind", kind!))));
        }

        return Task.CompletedTask;
    }
}
