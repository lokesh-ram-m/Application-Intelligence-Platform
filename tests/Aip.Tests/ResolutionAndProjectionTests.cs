using Aip.Abstractions.Knowledge;
using Aip.Abstractions.Projections;
using Aip.Ai;
using Aip.Core.Domain;
using Aip.Infrastructure;
using Aip.Knowledge;
using Aip.Projections;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aip.Tests;

internal static class Ids
{
    // Build identities through the builder so segment values (e.g. routes with '/') are encoded correctly.
    public static KnowledgeIdentity Of(string app, params (string Kind, string Value)[] segments)
    {
        KnowledgeIdentity id = KnowledgeIdentity.ForApplication(new ApplicationId(app));
        foreach ((string kind, string value) in segments) id = id.Append(new IdentitySegment(kind, value));

        return id;
    }
}

public class ResolutionTests
{
    private static Evidence Ev() => Evidence.Create(new RepositoryId("r"), new Commit("c"), "t", ExtractionMethod.Deterministic, Confidence.Full);

    private static KnowledgeNode Node(KnowledgeIdentity id, string kind, params (string, string)[] props)
    {
        var d = new Dictionary<string, string>();
        foreach ((string k, string v) in props) d[k] = v;

        return KnowledgeNode.Create(id, NodeKind.From(kind), new[] { Ev() }, Confidence.Full, d);
    }

    private static IRelationshipResolutionEngine Engine() =>
        new ServiceCollection().AddAipKnowledge().BuildServiceProvider().GetRequiredService<IRelationshipResolutionEngine>();

    [Fact]
    public async Task Angular_call_maps_to_backend_endpoint()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("endpoint", "GET /api/Customer")), "Endpoint", ("verb", "GET"), ("route", "/api/Customer")),
            Node(Ids.Of("A", ("repo", "web"), ("apicall", "GET /api/customer")), "ApiCall", ("verb", "GET"), ("url", "/api/customer")),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        Assert.Contains(resolved, r => r.Type.Value == "MAPS_TO");
    }

    [Fact]
    public async Task Suffix_match_resolves_when_the_candidate_endpoint_is_unique()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("endpoint", "GET /api/v1/clients")), "Endpoint", ("verb", "GET"), ("route", "/api/v1/clients")),
            Node(Ids.Of("A", ("repo", "web"), ("apicall", "GET /clients")), "ApiCall", ("verb", "GET"), ("url", "/clients")),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        Assert.Contains(resolved, r => r.Type.Value == "MAPS_TO");
    }

    [Fact]
    public async Task Suffix_match_is_skipped_when_multiple_endpoints_could_match()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("endpoint", "GET /api/v1/clients")), "Endpoint", ("verb", "GET"), ("route", "/api/v1/clients")),
            Node(Ids.Of("A", ("endpoint", "GET /api/v2/clients")), "Endpoint", ("verb", "GET"), ("route", "/api/v2/clients")),
            Node(Ids.Of("A", ("repo", "web"), ("apicall", "GET /clients")), "ApiCall", ("verb", "GET"), ("url", "/clients")),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        // Two endpoints share the suffix "/clients" — picking either one would be a coin-flip, so the
        // call is left unresolved rather than asserting a confident-looking false positive.
        Assert.DoesNotContain(resolved, r => r.Type.Value == "MAPS_TO");
    }

    [Fact]
    public async Task Service_and_datastore_in_same_project_are_linked()
    {
        var nodes = new List<KnowledgeNode>
        {
            Node(Ids.Of("A", ("repo", "b"), ("project", "Api"), ("type", "Ns.CustomerService")), "Service"),
            Node(Ids.Of("A", ("repo", "b"), ("project", "Api"), ("datastore", "ShopDbContext")), "DataStore"),
        };

        IReadOnlyList<RelationshipDiscovery> resolved = await Engine().ResolveAsync(nodes);

        Assert.Contains(resolved, r => r.Type.Value == "USES");
    }
}

public class ProjectionTests
{
    private static IProjectionEngine Engine()
    {
        var services = new ServiceCollection();
        services.AddAipInfrastructure(new ConfigurationBuilder().Build());
        services.AddAipAi();
        services.AddAipProjections();

        return services.BuildServiceProvider().GetRequiredService<IProjectionEngine>();
    }

    private static Evidence Ev() => Evidence.Create(new RepositoryId("r"), new Commit("c"), "t", ExtractionMethod.Deterministic, Confidence.Full);

    [Fact]
    public async Task Documentation_is_generated_from_the_snapshot_only()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity controller = Ids.Of("ShopApp", ("repo", "b"), ("type", "Api.CustomerController"));
        KnowledgeIdentity endpoint = Ids.Of("ShopApp", ("endpoint", "GET /api/Customer"));

        var nodes = new[]
        {
            KnowledgeNode.Create(controller, NodeKind.From("Controller"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CustomerController" }),
            KnowledgeNode.Create(endpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["route"] = "/api/Customer" }),
        };
        var rels = new[] { Relationship.Create(RelationshipType.From("EXPOSES"), controller, endpoint, new[] { Ev() }, Confidence.Full) };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        var artifacts = results.SelectMany(r => r.Artifacts).ToList();

        Assert.Contains(artifacts, a => a.Name == "product-specification/overview.md");
        Assert.Contains(artifacts, a => a.Name == "technical-specification/api-reference.md" && a.Content.Contains("/api/Customer"));
        Assert.Contains(artifacts, a => a.Name == "technical-specification/architecture.md" && a.Content.Contains("EXPOSES"));
    }

    [Fact]
    public async Task Component_nothing_renders_is_flagged_but_a_composed_one_is_not()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity header = Ids.Of("ShopApp", ("component", "Header"));
        KnowledgeIdentity orphan = Ids.Of("ShopApp", ("component", "OrphanWidget"));
        KnowledgeIdentity shell = Ids.Of("ShopApp", ("component", "AppShell"));

        var nodes = new[]
        {
            KnowledgeNode.Create(shell, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "AppShell" }),
            KnowledgeNode.Create(header, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "Header" }),
            KnowledgeNode.Create(orphan, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "OrphanWidget" }),
        };
        // Only AppShell -> Header is composed; OrphanWidget has no incoming RENDERS edge from anywhere.
        var rels = new[] { Relationship.Create(RelationshipType.From("RENDERS"), shell, header, new[] { Ev() }, Confidence.Full) };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string frontend = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/frontend.md").Content;

        Assert.Contains("OrphanWidget** _(not rendered by anything else in the codebase)_", frontend);
        Assert.DoesNotContain("Header** _(not rendered by anything else in the codebase)_", frontend);
    }

    [Fact]
    public async Task Read_only_token_key_and_deployment_base_path_are_flagged_on_the_security_page()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity readOnly = Ids.Of("ShopApp", ("tokenstorage", "localStorage:UserAccessToken"));
        KnowledgeIdentity roundTrip = Ids.Of("ShopApp", ("tokenstorage", "localStorage:cms_active_role"));
        KnowledgeIdentity basePath = Ids.Of("ShopApp", ("configuration", "deployment-base-path"));

        var nodes = new[]
        {
            KnowledgeNode.Create(readOnly, NodeKind.From("TokenStorage"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["location"] = "localStorage", ["key"] = "UserAccessToken", ["operation"] = "get" }),
            KnowledgeNode.Create(roundTrip, NodeKind.From("TokenStorage"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["location"] = "localStorage", ["key"] = "cms_active_role", ["operation"] = "get+set" }),
            KnowledgeNode.Create(basePath, NodeKind.From("Configuration"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "Frontend deployment base path", ["value"] = "/cms-ui/" }),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, Array.Empty<Relationship>());

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string security = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/security.md").Content;

        Assert.Contains("`UserAccessToken` (in localStorage)", security);
        Assert.DoesNotContain("`cms_active_role`", security);
        Assert.Contains("sub-path `/cms-ui/`", security);
    }

    [Fact]
    public async Task Two_resolved_component_to_endpoint_chains_render_a_sequence_diagram()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity productList = Ids.Of("ShopApp", ("component", "ProductList"));
        KnowledgeIdentity orderForm = Ids.Of("ShopApp", ("component", "OrderForm"));
        KnowledgeIdentity getProductsCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "GET /api/products"));
        KnowledgeIdentity postOrderCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "POST /api/orders"));
        KnowledgeIdentity productsEndpoint = Ids.Of("ShopApp", ("endpoint", "GET /api/Products"));
        KnowledgeIdentity ordersEndpoint = Ids.Of("ShopApp", ("endpoint", "POST /api/Orders"));
        KnowledgeIdentity productsController = Ids.Of("ShopApp", ("repo", "b"), ("type", "Api.ProductsController"));

        var nodes = new[]
        {
            KnowledgeNode.Create(productList, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "ProductList" }),
            KnowledgeNode.Create(orderForm, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "OrderForm" }),
            KnowledgeNode.Create(getProductsCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["url"] = "/api/products" }),
            KnowledgeNode.Create(postOrderCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "POST", ["url"] = "/api/orders" }),
            KnowledgeNode.Create(productsEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["route"] = "/api/Products" }),
            KnowledgeNode.Create(ordersEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "POST", ["route"] = "/api/Orders" }),
            KnowledgeNode.Create(productsController, NodeKind.From("Controller"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "ProductsController" }),
        };
        var rels = new[]
        {
            Relationship.Create(RelationshipType.From("CALLS"), productList, getProductsCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("CALLS"), orderForm, postOrderCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), getProductsCall, productsEndpoint, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), postOrderCall, ordersEndpoint, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("EXPOSES"), productsController, productsEndpoint, new[] { Ev() }, Confidence.Full),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string architecture = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/architecture.md").Content;

        Assert.Contains("## Request flows", architecture);
        Assert.Contains("sequenceDiagram", architecture);
        Assert.Contains("participant ProductList as ProductList", architecture);
        Assert.Contains("participant OrderForm as OrderForm", architecture);
        Assert.Contains("GET /api/Products (ProductsController)", architecture);
        Assert.Contains("POST /api/Orders", architecture);
    }

    [Fact]
    public async Task A_dispatched_command_extends_the_request_flow_through_its_handler_and_dependency()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity productList = Ids.Of("ShopApp", ("component", "ProductList"));
        KnowledgeIdentity orderForm = Ids.Of("ShopApp", ("component", "OrderForm"));
        KnowledgeIdentity getProductsCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "GET /api/products"));
        KnowledgeIdentity postOrderCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "POST /api/orders"));
        KnowledgeIdentity productsEndpoint = Ids.Of("ShopApp", ("endpoint", "GET /api/Products"));
        KnowledgeIdentity ordersEndpoint = Ids.Of("ShopApp", ("endpoint", "POST /api/Orders"));
        // The CQRS dispatch-site chain: Endpoint --DISPATCHES--> Command, Handler --HANDLES--> Command,
        // Handler --DEPENDS_ON--> Repository — three independently-resolved facts (MediatorDispatch,
        // CqrsAnalyzer, the general dependency walker), never a single analyzer inventing the whole thing.
        KnowledgeIdentity createOrderCommand = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.CreateOrderCommand"));
        KnowledgeIdentity createOrderHandler = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.CreateOrderCommandHandler"));
        KnowledgeIdentity orderRepository = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.OrderRepository"));

        var nodes = new[]
        {
            KnowledgeNode.Create(productList, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "ProductList" }),
            KnowledgeNode.Create(orderForm, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "OrderForm" }),
            KnowledgeNode.Create(getProductsCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["url"] = "/api/products" }),
            KnowledgeNode.Create(postOrderCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "POST", ["url"] = "/api/orders" }),
            KnowledgeNode.Create(productsEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["route"] = "/api/Products" }),
            KnowledgeNode.Create(ordersEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "POST", ["route"] = "/api/Orders" }),
            KnowledgeNode.Create(createOrderCommand, NodeKind.From("Command"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CreateOrderCommand" }),
            KnowledgeNode.Create(createOrderHandler, NodeKind.From("Handler"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CreateOrderCommandHandler" }),
            KnowledgeNode.Create(orderRepository, NodeKind.From("Repository"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "OrderRepository" }),
        };
        var rels = new[]
        {
            Relationship.Create(RelationshipType.From("CALLS"), productList, getProductsCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("CALLS"), orderForm, postOrderCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), getProductsCall, productsEndpoint, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), postOrderCall, ordersEndpoint, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("DISPATCHES"), ordersEndpoint, createOrderCommand, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("HANDLES"), createOrderHandler, createOrderCommand, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("DEPENDS_ON"), createOrderHandler, orderRepository, new[] { Ev() }, Confidence.Full),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string architecture = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/architecture.md").Content;

        Assert.Contains("## Request flows", architecture);
        // The products chain (no CQRS dispatch) still renders as a plain two-hop call.
        Assert.Contains("participant ProductList as ProductList", architecture);
        // The orders chain extends through the handler and its dependency.
        Assert.Contains("participant OrderForm as OrderForm", architecture);
        Assert.Contains("as CreateOrderCommandHandler", architecture);
        Assert.Contains("as OrderRepository", architecture);
        Assert.Contains("CreateOrderCommand", architecture);
        Assert.Contains("dispatched through a CQRS mediator", architecture);
    }

    [Fact]
    public async Task Database_operations_thread_through_every_handler_dependency_not_just_the_first()
    {
        // Reproduces the exact flow this feature was built for:
        //   POST /orders → CreateOrderCommand → CreateOrderHandler
        //     → CustomerRepository → READ Customer
        //     → OrderRepository → INSERT Order, INSERT OrderItem, SaveChangesAsync
        // Two DEPENDS_ON edges from the same handler — this is what confirms the "not just the first
        // dependency" fix, since the earlier version of this rendering only ever walked one.
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity productList = Ids.Of("ShopApp", ("component", "ProductList"));
        KnowledgeIdentity orderForm = Ids.Of("ShopApp", ("component", "OrderForm"));
        KnowledgeIdentity getProductsCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "GET /api/products"));
        KnowledgeIdentity postOrderCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "POST /api/orders"));
        KnowledgeIdentity productsEndpoint = Ids.Of("ShopApp", ("endpoint", "GET /api/Products"));
        KnowledgeIdentity ordersEndpoint = Ids.Of("ShopApp", ("endpoint", "POST /api/Orders"));
        KnowledgeIdentity createOrderCommand = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.CreateOrderCommand"));
        KnowledgeIdentity createOrderHandler = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.CreateOrderCommandHandler"));
        KnowledgeIdentity customerRepository = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.CustomerRepository"));
        KnowledgeIdentity orderRepository = Ids.Of("ShopApp", ("repo", "b"), ("type", "Orders.OrderRepository"));
        KnowledgeIdentity opReadCustomer = Ids.Of("ShopApp", ("dboperation", "read-customer"));
        KnowledgeIdentity opInsertOrder = Ids.Of("ShopApp", ("dboperation", "insert-order"));
        KnowledgeIdentity opInsertOrderItem = Ids.Of("ShopApp", ("dboperation", "insert-orderitem"));
        KnowledgeIdentity opSaveChanges = Ids.Of("ShopApp", ("dboperation", "save-changes"));

        var nodes = new[]
        {
            KnowledgeNode.Create(productList, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "ProductList" }),
            KnowledgeNode.Create(orderForm, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "OrderForm" }),
            KnowledgeNode.Create(getProductsCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["url"] = "/api/products" }),
            KnowledgeNode.Create(postOrderCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "POST", ["url"] = "/api/orders" }),
            KnowledgeNode.Create(productsEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["route"] = "/api/Products" }),
            KnowledgeNode.Create(ordersEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "POST", ["route"] = "/api/orders" }),
            KnowledgeNode.Create(createOrderCommand, NodeKind.From("Command"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CreateOrderCommand" }),
            KnowledgeNode.Create(createOrderHandler, NodeKind.From("Handler"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CreateOrderCommandHandler" }),
            KnowledgeNode.Create(customerRepository, NodeKind.From("Repository"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "CustomerRepository" }),
            KnowledgeNode.Create(orderRepository, NodeKind.From("Repository"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "OrderRepository" }),
            KnowledgeNode.Create(opReadCustomer, NodeKind.From("DatabaseOperation"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["operation"] = "Read", ["entity"] = "Customer", ["method"] = "FirstOrDefaultAsync", ["owner"] = "CustomerRepository", ["callerMethod"] = "GetById" }),
            KnowledgeNode.Create(opInsertOrder, NodeKind.From("DatabaseOperation"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["operation"] = "Insert", ["entity"] = "Order", ["method"] = "Add", ["owner"] = "OrderRepository", ["callerMethod"] = "Create" }),
            KnowledgeNode.Create(opInsertOrderItem, NodeKind.From("DatabaseOperation"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["operation"] = "Insert", ["entity"] = "OrderItem", ["method"] = "Add", ["owner"] = "OrderRepository", ["callerMethod"] = "Create" }),
            KnowledgeNode.Create(opSaveChanges, NodeKind.From("DatabaseOperation"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["operation"] = "Persist", ["method"] = "SaveChangesAsync", ["owner"] = "OrderRepository", ["callerMethod"] = "Create" }),
        };
        var rels = new[]
        {
            Relationship.Create(RelationshipType.From("CALLS"), productList, getProductsCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("CALLS"), orderForm, postOrderCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), getProductsCall, productsEndpoint, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), postOrderCall, ordersEndpoint, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("DISPATCHES"), ordersEndpoint, createOrderCommand, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("HANDLES"), createOrderHandler, createOrderCommand, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("DEPENDS_ON"), createOrderHandler, customerRepository, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("DEPENDS_ON"), createOrderHandler, orderRepository, new[] { Ev() }, Confidence.Full),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string architecture = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/architecture.md").Content;

        // Both dependencies show up in the diagram, not just the first one DEPENDS_ON happened to list.
        Assert.Contains("as CustomerRepository", architecture);
        Assert.Contains("as OrderRepository", architecture);
        Assert.Contains("participant DB as Database", architecture);
        Assert.Contains("READ Customer", architecture);
        Assert.Contains("INSERT Order", architecture);
        Assert.Contains("INSERT OrderItem", architecture);
        Assert.Contains("SaveChangesAsync", architecture);

        // The flat arrow-chain summary reproduces the exact shape asked for.
        Assert.Contains("Database operations reached from each flow:", architecture);
        Assert.Contains("POST /api/orders", architecture);
        Assert.Contains("→ CreateOrderCommand", architecture);
        Assert.Contains("→ CreateOrderCommandHandler", architecture);
        Assert.Contains("→ CustomerRepository", architecture);
        Assert.Contains("→ OrderRepository", architecture);
        Assert.Contains("  → READ Customer", architecture);
        Assert.Contains("  → INSERT Order", architecture);
        Assert.Contains("  → INSERT OrderItem", architecture);
        Assert.Contains("  → SaveChangesAsync", architecture);
    }

    [Fact]
    public async Task A_single_resolved_chain_does_not_earn_a_sequence_diagram()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity productList = Ids.Of("ShopApp", ("component", "ProductList"));
        KnowledgeIdentity getProductsCall = Ids.Of("ShopApp", ("repo", "web"), ("apicall", "GET /api/products"));
        KnowledgeIdentity productsEndpoint = Ids.Of("ShopApp", ("endpoint", "GET /api/Products"));

        var nodes = new[]
        {
            KnowledgeNode.Create(productList, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "ProductList" }),
            KnowledgeNode.Create(getProductsCall, NodeKind.From("ApiCall"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["url"] = "/api/products" }),
            KnowledgeNode.Create(productsEndpoint, NodeKind.From("Endpoint"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["verb"] = "GET", ["route"] = "/api/Products" }),
        };
        var rels = new[]
        {
            Relationship.Create(RelationshipType.From("CALLS"), productList, getProductsCall, new[] { Ev() }, Confidence.Full),
            Relationship.Create(RelationshipType.From("MAPS_TO"), getProductsCall, productsEndpoint, new[] { Ev() }, Confidence.Full),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, rels);

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string architecture = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/architecture.md").Content;

        Assert.DoesNotContain("## Request flows", architecture);
    }

    [Fact]
    public async Task Status_workflow_states_are_listed_without_a_fabricated_transition_diagram()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity workflow = Ids.Of("ShopApp", ("project", "Api"), ("workflow", "Order:OrderStatus"));

        var nodes = new[]
        {
            KnowledgeNode.Create(workflow, NodeKind.From("StatusWorkflow"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "OrderStatus", ["owner"] = "Order", ["values"] = "Pending, Shipped, Cancelled" }),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, Array.Empty<Relationship>());

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string architecture = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/architecture.md").Content;

        Assert.Contains("## Status workflows", architecture);
        Assert.Contains("**Order.OrderStatus** — states: `Pending`, `Shipped`, `Cancelled`", architecture);
        // No transition order is grounded in the data, so no mermaid stateDiagram should be fabricated.
        Assert.DoesNotContain("stateDiagram", architecture);
    }

    [Fact]
    public async Task Validator_rules_business_rules_and_audit_logging_are_rendered_on_the_architecture_page()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity validator = Ids.Of("ShopApp", ("type", "OrderValidator"));
        KnowledgeIdentity businessRule = Ids.Of("ShopApp", ("project", "Api"), ("businessrule", "OrderService.ValidateStock:42"));
        KnowledgeIdentity auditLog = Ids.Of("ShopApp", ("project", "Api"), ("auditlog", "OrderService:Order"));

        var nodes = new[]
        {
            KnowledgeNode.Create(validator, NodeKind.From("Validator"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "OrderValidator", ["rules"] = "CustomerId: NotEmpty; Total: GreaterThan(0)" }),
            KnowledgeNode.Create(businessRule, NodeKind.From("BusinessRule"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["rule"] = "Insufficient stock for order", ["owner"] = "OrderService", ["method"] = "ValidateStock" }),
            KnowledgeNode.Create(auditLog, NodeKind.From("AuditLog"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["entityType"] = "Order", ["source"] = "OrderService" }),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, Array.Empty<Relationship>());

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string architecture = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/architecture.md").Content;

        Assert.Contains("## Validation rules", architecture);
        Assert.Contains("**OrderValidator**", architecture);
        Assert.Contains("- CustomerId: NotEmpty", architecture);
        Assert.Contains("- Total: GreaterThan(0)", architecture);

        Assert.Contains("## Business rules", architecture);
        Assert.Contains("**OrderService**", architecture);
        Assert.Contains("`ValidateStock` — Insufficient stock for order", architecture);

        Assert.Contains("## Audit logging", architecture);
        Assert.Contains("**Order** — logged by OrderService", architecture);
    }

    [Fact]
    public async Task Backend_and_frontend_Filter_nodes_are_never_conflated_in_rendering()
    {
        var app = new ApplicationId("ShopApp");
        // Backend MVC filter TYPE (ASP.NET Core FilterAnalyzer's shape: kind, no component).
        KnowledgeIdentity mvcFilter = Ids.Of("ShopApp", ("project", "Api"), ("type", "AuditFilter"));
        // Frontend UI filter STATE (ReactFilterAnalyzer's redesigned shape: component + kind + targetField).
        KnowledgeIdentity singleValue = Ids.Of("ShopApp", ("filter", "ContractsComponent:filterContractId"));
        KnowledgeIdentity multiSelect = Ids.Of("ShopApp", ("filter", "ContractsComponent:selectedStatusFilters"));
        // FrontendPage is only generated when a real frontend is present (UIComponent/Route/UIService) —
        // without this, the Filter nodes alone wouldn't be enough to produce frontend.md at all.
        KnowledgeIdentity contractsComponent = Ids.Of("ShopApp", ("component", "ContractsComponent"));

        var nodes = new[]
        {
            KnowledgeNode.Create(contractsComponent, NodeKind.From("UIComponent"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "ContractsComponent" }),
            KnowledgeNode.Create(mvcFilter, NodeKind.From("Filter"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "AuditFilter", ["kind"] = "authorization" }),
            KnowledgeNode.Create(singleValue, NodeKind.From("Filter"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "filterContractId", ["component"] = "ContractsComponent", ["kind"] = "single-value", ["targetField"] = "contractId" }),
            KnowledgeNode.Create(multiSelect, NodeKind.From("Filter"), new[] { Ev() }, Confidence.Full,
                new Dictionary<string, string> { ["name"] = "selectedStatusFilters", ["component"] = "ContractsComponent", ["kind"] = "multi-select" }),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, Array.Empty<Relationship>());

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        var artifacts = results.SelectMany(r => r.Artifacts).ToList();
        string architecture = artifacts.Single(a => a.Name == "technical-specification/architecture.md").Content;
        string frontend = artifacts.Single(a => a.Name == "technical-specification/frontend.md").Content;

        // Backend page: grouped by kind, only the real MVC filter — never the raw frontend state names.
        Assert.Contains("**Request filters:** authorization (AuditFilter)", architecture);
        Assert.DoesNotContain("filterContractId", architecture);
        Assert.DoesNotContain("selectedStatusFilters", architecture);

        // Frontend page: grouped by component and shape, preferring the traced targetField over the raw name.
        Assert.Contains("**ContractsComponent** — filters by contractId; multi-select: selectedStatusFilters", frontend);
        Assert.DoesNotContain("AuditFilter", frontend);
    }

    [Fact]
    public async Task Two_roles_with_reachable_routes_render_a_journey_diagram()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity admin = Ids.Of("ShopApp", ("role", "Admin"));
        KnowledgeIdentity customer = Ids.Of("ShopApp", ("role", "Customer"));
        KnowledgeIdentity adminRoute = Ids.Of("ShopApp", ("route", "/admin/users"));
        KnowledgeIdentity ordersRoute = Ids.Of("ShopApp", ("route", "/orders/:id"));

        var nodes = new[]
        {
            KnowledgeNode.Create(admin, NodeKind.From("Role"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "Admin", ["value"] = "admin" }),
            KnowledgeNode.Create(customer, NodeKind.From("Role"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "Customer", ["value"] = "customer" }),
            KnowledgeNode.Create(adminRoute, NodeKind.From("Route"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["path"] = "/admin/users", ["protected"] = "yes", ["roles"] = "Admin" }),
            KnowledgeNode.Create(ordersRoute, NodeKind.From("Route"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["path"] = "/orders/:id", ["protected"] = "yes", ["roles"] = "Customer" }),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, Array.Empty<Relationship>());

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string frontend = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/frontend.md").Content;

        Assert.Contains("## User journeys (by role)", frontend);
        Assert.Contains("journey", frontend);
        Assert.Contains("section Admin", frontend);
        Assert.Contains("section Customer", frontend);
        Assert.Contains("/admin/users: 3: Admin", frontend);
        // ':' in a route param would break mermaid's own "task: score: actor" delimiter parsing.
        Assert.Contains("/orders/-id: 3: Customer", frontend);
    }

    [Fact]
    public async Task A_single_role_with_a_single_route_does_not_earn_a_journey_diagram()
    {
        var app = new ApplicationId("ShopApp");
        KnowledgeIdentity admin = Ids.Of("ShopApp", ("role", "Admin"));
        KnowledgeIdentity adminRoute = Ids.Of("ShopApp", ("route", "/admin/users"));

        var nodes = new[]
        {
            KnowledgeNode.Create(admin, NodeKind.From("Role"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["name"] = "Admin", ["value"] = "admin" }),
            KnowledgeNode.Create(adminRoute, NodeKind.From("Route"), new[] { Ev() }, Confidence.Full, new Dictionary<string, string> { ["path"] = "/admin/users", ["protected"] = "yes", ["roles"] = "Admin" }),
        };
        Snapshot snapshot = Snapshot.Create(SnapshotId.New(), app, System.DateTimeOffset.UtcNow, nodes, Array.Empty<Relationship>());

        IReadOnlyList<ProjectionResult> results = await Engine().RunAsync(snapshot);
        string frontend = results.SelectMany(r => r.Artifacts).Single(a => a.Name == "technical-specification/frontend.md").Content;

        Assert.DoesNotContain("## User journeys", frontend);
    }
}
