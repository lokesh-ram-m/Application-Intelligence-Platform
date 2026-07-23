using Aip.Abstractions.Analysis;
using Aip.Abstractions.Engines;
using Aip.Core.Domain;
using Aip.Engines.Roslyn;
using Aip.Plugins.AspNetCore;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Xunit;

namespace Aip.Tests;

/// <summary>
/// Exercises ControllerAnalyzer/MinimalApiAnalyzer's mediator-dispatch detection (the DISPATCHES
/// relationship) against a real, in-memory Roslyn compilation — not a hand-built Snapshot. Constructor-
/// injection-based DEPENDS_ON tracking can never connect a Controller/Endpoint to the Command/Query it
/// dispatches (IMediator/ISender are external interfaces, not in-source types), so this closes that gap
/// via semantic analysis of the `mediator.Send(request)` call site itself. No real MediatR NuGet package is
/// referenced — detection matches by interface name/shape (IMediator/ISender/IRequest), so a minimal
/// in-source fake of those interfaces is enough to exercise the real analyzer code.
/// </summary>
public class CqrsDispatchAnalyzerTests
{
    private const string FakeFramework = """
        namespace System.Web
        {
            public class ApiControllerAttribute : System.Attribute { }
            public class ControllerBase { }
            public class HttpPostAttribute : System.Attribute { public HttpPostAttribute(string template = null) { } }
            public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template = null) { } }
            public class FromBodyAttribute : System.Attribute { }
            public class FromRouteAttribute : System.Attribute { }
            public class FromQueryAttribute : System.Attribute { }
        }
        namespace MediatR
        {
            public interface IRequest { }
            public interface IRequest<TResponse> { }
            public interface IRequestHandler<TRequest, TResponse> { }
            public interface INotification { }
            public interface ISender
            {
                System.Threading.Tasks.Task Send(object request, System.Threading.CancellationToken ct = default);
            }
            public interface IPublisher
            {
                System.Threading.Tasks.Task Publish(object notification, System.Threading.CancellationToken ct = default);
            }
            public interface IMediator : ISender, IPublisher { }
        }
        """;

    private sealed class FakeSink : IDiscoverySink
    {
        public List<NodeDiscovery> Nodes { get; } = new();
        public List<RelationshipDiscovery> Relationships { get; } = new();

        public void Add(Discovery discovery)
        {
            if (discovery is NodeDiscovery n) Nodes.Add(n);
            else if (discovery is RelationshipDiscovery r) Relationships.Add(r);
        }

        public void Report(Core.Domain.Diagnostic diagnostic) { }
    }

    private sealed class FakeContext : IAnalysisContext
    {
        public FakeContext(RoslynSemanticModel model, string projectName) =>
            (Model, Artifact) = (model, new Artifact(new RepositoryId("r"), "x", "dotnet-project", projectName));

        public ExecutionId ExecutionId => new(Guid.NewGuid());
        public ExecutionScope Scope => new(new ApplicationId("app"), new[] { new RepositoryId("r") }, Array.Empty<string>(), ExecutionMode.Local, null);
        public Artifact Artifact { get; }
        public RepositoryId Repository => new("r");
        public Commit Commit => new("c");
        public string Engine => "roslyn";
        public ISemanticModel Model { get; }

        public KnowledgeIdentity NodeId(params IdentitySegment[] tail)
        {
            KnowledgeIdentity id = KnowledgeIdentity.ForApplication(new ApplicationId("app"));
            foreach (IdentitySegment seg in tail) id = id.Append(seg);
            return id;
        }

        public KnowledgeIdentity AppNodeId(params IdentitySegment[] tail) => NodeId(tail);

        public Evidence Evidence(string? file = null, int? line = null, string? symbol = null) =>
            Core.Domain.Evidence.Create(Repository, Commit, Engine, ExtractionMethod.Deterministic, Confidence.Full);
    }

    private static readonly MetadataReference[] References = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
        .ToArray();

    private static RoslynSemanticModel Compile(string source)
    {
        // A bare filename (no directory) — or a directory that doesn't actually exist on disk — trips
        // Sym.ProjectOf's DirectoryInfo.GetFiles walk. Use the process's real temp directory (guaranteed to
        // exist) as the file's location; walking up from there just finds no .csproj and returns null,
        // which ProjectOf already falls back from gracefully.
        SyntaxTree tree = CSharpSyntaxTree.ParseText(FakeFramework + "\n" + source, path: Path.Combine(Path.GetTempPath(), "Test.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("TestAssembly", new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new RoslynSemanticModel(compilation, new[] { tree });
    }

    [Fact]
    public async Task Controller_action_dispatching_via_IMediator_gets_a_DISPATCHES_edge_to_the_command()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp.Controllers
            {
                [System.Web.ApiController]
                public class ThingsController : System.Web.ControllerBase
                {
                    private readonly MediatR.IMediator _mediator;
                    public ThingsController(MediatR.IMediator mediator) { _mediator = mediator; }

                    [System.Web.HttpPost]
                    public System.Threading.Tasks.Task Create()
                    {
                        return _mediator.Send(new TestApp.Commands.CreateThingCommand());
                    }
                }
            }
            namespace TestApp.Commands
            {
                public class CreateThingCommand : MediatR.IRequest { }
            }
            """);

        var sink = new FakeSink();
        await new ControllerAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery endpoint = Assert.Single(sink.Nodes, n => n.Kind.Value == "Endpoint");
        RelationshipDiscovery dispatch = Assert.Single(sink.Relationships, r => r.Type.Value == "DISPATCHES");
        Assert.Equal(endpoint.Identity, dispatch.From);
        Assert.Equal("TestApp.Commands.CreateThingCommand", dispatch.To.ShortName);
    }

    [Fact]
    public async Task Controller_action_return_type_and_parameter_binding_sources_are_captured()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp.Controllers
            {
                [System.Web.ApiController]
                public class OrdersController : System.Web.ControllerBase
                {
                    [System.Web.HttpPost]
                    public System.Threading.Tasks.Task<string> Create(
                        [System.Web.FromBody] TestApp.Dtos.CreateOrderDto dto,
                        [System.Web.FromRoute] int tenantId,
                        System.Threading.CancellationToken ct)
                    {
                        return System.Threading.Tasks.Task.FromResult("ok");
                    }
                }
            }
            namespace TestApp.Dtos
            {
                public class CreateOrderDto { }
            }
            """);

        var sink = new FakeSink();
        await new ControllerAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery endpoint = Assert.Single(sink.Nodes, n => n.Kind.Value == "Endpoint");
        Assert.Equal("Task<string>", endpoint.Properties["returns"]);
        Assert.Equal("dto: CreateOrderDto (body); tenantId: int (route); ct: CancellationToken", endpoint.Properties["parameters"]);
    }

    [Fact]
    public async Task Handler_publishing_a_notification_via_IMediator_gets_a_DISPATCHES_edge()
    {
        // The realistic shape this exists for: a command handler that isn't an endpoint at all, publishing
        // a domain event once it's done its own work — MediatorPublishAnalyzer has to find this without any
        // help from ControllerAnalyzer/MinimalApiAnalyzer, since neither ever sees this class.
        RoslynSemanticModel model = Compile("""
            namespace TestApp.Handlers
            {
                public class CreateOrderCommandHandler
                {
                    private readonly MediatR.IMediator _mediator;
                    public CreateOrderCommandHandler(MediatR.IMediator mediator) { _mediator = mediator; }

                    public System.Threading.Tasks.Task Handle()
                    {
                        return _mediator.Publish(new TestApp.Events.OrderCreatedEvent());
                    }
                }
            }
            namespace TestApp.Events
            {
                public class OrderCreatedEvent : MediatR.INotification { }
            }
            """);

        var sink = new FakeSink();
        await new MediatorPublishAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        RelationshipDiscovery dispatch = Assert.Single(sink.Relationships, r => r.Type.Value == "DISPATCHES");
        Assert.Equal("TestApp.Handlers.CreateOrderCommandHandler", dispatch.From.ShortName);
        Assert.Equal("TestApp.Events.OrderCreatedEvent", dispatch.To.ShortName);
    }

    [Fact]
    public async Task An_unrelated_Send_method_on_a_non_mediator_receiver_is_not_mistaken_for_a_dispatch()
    {
        // Same shape (a "Send" call inside a controller action) but the receiver is an ordinary HTTP
        // client, not IMediator/ISender — must not produce a DISPATCHES edge.
        RoslynSemanticModel model = Compile("""
            namespace TestApp.Controllers
            {
                public class Client { public System.Threading.Tasks.Task Send(object request) => null; }

                [System.Web.ApiController]
                public class ThingsController : System.Web.ControllerBase
                {
                    private readonly Client _client;
                    public ThingsController(Client client) { _client = client; }

                    [System.Web.HttpPost]
                    public System.Threading.Tasks.Task Create() => _client.Send(new object());
                }
            }
            """);

        var sink = new FakeSink();
        await new ControllerAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        Assert.DoesNotContain(sink.Relationships, r => r.Type.Value == "DISPATCHES");
    }

    [Fact]
    public async Task Minimal_API_inline_lambda_dispatching_via_ISender_gets_a_DISPATCHES_edge()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp.Commands
            {
                public class CreateThingCommand : MediatR.IRequest { }
            }
            public static class Program
            {
                public static void Main()
                {
                    var app = new WebApplication();
                    app.MapPost("/things", async (MediatR.ISender sender) =>
                        await sender.Send(new TestApp.Commands.CreateThingCommand()));
                }
            }
            public class WebApplication
            {
                public void MapPost(string route, System.Func<MediatR.ISender, System.Threading.Tasks.Task> handler) { }
            }
            """);

        var sink = new FakeSink();
        await new MinimalApiAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery endpoint = Assert.Single(sink.Nodes, n => n.Kind.Value == "Endpoint");
        RelationshipDiscovery dispatch = Assert.Single(sink.Relationships, r => r.Type.Value == "DISPATCHES");
        Assert.Equal(endpoint.Identity, dispatch.From);
        Assert.Equal("TestApp.Commands.CreateThingCommand", dispatch.To.ShortName);
    }

    [Fact]
    public async Task Two_argument_DI_registration_captures_the_lifetime()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public interface IOrderRepository { }
                public class OrderRepository : IOrderRepository { }
                public class Startup
                {
                    public void Configure(FakeServiceCollection services) =>
                        services.AddScoped<IOrderRepository, OrderRepository>();
                }
            }
            public class FakeServiceCollection
            {
                public FakeServiceCollection AddScoped<TI, TImpl>() where TImpl : TI => this;
            }
            """);

        var sink = new FakeSink();
        await new DependencyInjectionAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery impl = Assert.Single(sink.Nodes, n => n.Kind.Value == "Repository");
        Assert.Equal("Scoped", impl.Properties["lifetime"]);
        Assert.Contains(sink.Relationships, r => r.Type.Value == "IMPLEMENTS" && r.From.Equals(impl.Identity));
    }

    [Fact]
    public async Task Factory_DI_registration_resolves_the_implementation_from_the_lambda_body()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public interface IClock { }
                public class SystemClock : IClock { }
                public class Startup
                {
                    public void Configure(FakeServiceCollection services) =>
                        services.AddSingleton<IClock>(sp => new SystemClock());
                }
            }
            public class FakeServiceCollection
            {
                public FakeServiceCollection AddSingleton<TI>(System.Func<object, TI> factory) => this;
            }
            """);

        var sink = new FakeSink();
        await new DependencyInjectionAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        NodeDiscovery impl = Assert.Single(sink.Nodes, n => n.Properties["name"] == "SystemClock");
        Assert.Equal("Singleton", impl.Properties["lifetime"]);
        Assert.Contains(sink.Relationships, r => r.Type.Value == "IMPLEMENTS" && r.From.Equals(impl.Identity) && r.To.ShortName == "TestApp.IClock");
    }

    [Fact]
    public async Task Open_generic_DI_registration_is_captured()
    {
        RoslynSemanticModel model = Compile("""
            namespace TestApp
            {
                public interface IRepository<T> { }
                public class Repository<T> : IRepository<T> { }
                public class Startup
                {
                    public void Configure(FakeServiceCollection services) =>
                        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
                }
            }
            public class FakeServiceCollection
            {
                public FakeServiceCollection AddScoped(System.Type iface, System.Type impl) => this;
            }
            """);

        var sink = new FakeSink();
        await new DependencyInjectionAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);

        Assert.Contains(sink.Relationships, r => r.Type.Value == "IMPLEMENTS");
        NodeDiscovery impl = Assert.Single(sink.Nodes);
        Assert.Equal("Scoped", impl.Properties["lifetime"]);
    }
}
