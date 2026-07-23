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
/// Exercises DatabaseOperationAnalyzer against a real, in-memory Roslyn compilation. The precision guards
/// are the actual point of this analyzer — a false "this is a database call" is worse than a missed one —
/// so the negative tests here (List&lt;T&gt;.Add, an unrelated Query() method, SaveChanges on an unrelated
/// type) matter as much as the positive ones.
/// </summary>
public class DatabaseOperationAnalyzerTests
{
    // A minimal in-source stand-in for Microsoft.EntityFrameworkCore — DbSet<T>/DbContext detection is
    // gated on the TYPE actually being named "DbSet", not on any variable/property naming convention, so
    // the fake needs the same shape (a generic DbSet<T> class, a DbContext base with DbSet<T> properties).
    private const string EfFakeFramework = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbSet<T> : System.Collections.Generic.IEnumerable<T>
            {
                public T Add(T entity) => entity;
                public void AddRange(System.Collections.Generic.IEnumerable<T> entities) { }
                public T Remove(T entity) => entity;
                public System.Threading.Tasks.Task<T> FindAsync(object id) => null;
                public System.Collections.Generic.IEnumerator<T> GetEnumerator() => null;
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null;
            }
            public class DatabaseFacade
            {
                public System.Threading.Tasks.Task<object> BeginTransactionAsync() => null;
            }
            public class DbContext
            {
                public DatabaseFacade Database => new DatabaseFacade();
                public int SaveChanges() => 0;
                public System.Threading.Tasks.Task<int> SaveChangesAsync() => null;
            }
        }
        namespace Microsoft.EntityFrameworkCore
        {
            public static class EntityFrameworkQueryableExtensions
            {
                public static System.Collections.Generic.List<T> ToList<T>(this System.Collections.Generic.IEnumerable<T> source) => null;
                public static System.Threading.Tasks.Task<System.Collections.Generic.List<T>> ToListAsync<T>(this System.Collections.Generic.IEnumerable<T> source) => null;
                public static System.Collections.Generic.IEnumerable<T> Where<T>(this System.Collections.Generic.IEnumerable<T> source, System.Func<T, bool> predicate) => source;
                public static System.Collections.Generic.IEnumerable<T> Include<T>(this System.Collections.Generic.IEnumerable<T> source, string path) => source;
                public static System.Collections.Generic.IEnumerable<T> AsNoTracking<T>(this System.Collections.Generic.IEnumerable<T> source) => source;
                public static System.Threading.Tasks.Task<T> FirstOrDefaultAsync<T>(this System.Collections.Generic.IEnumerable<T> source) => null;
            }
        }
        """;

    private sealed class FakeSink : IDiscoverySink
    {
        public List<NodeDiscovery> Nodes { get; } = new();
        public void Add(Discovery discovery) { if (discovery is NodeDiscovery n) Nodes.Add(n); }
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

    private static readonly MetadataReference[] BaseReferences = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
        .ToArray();

    // A real, separately-compiled assembly literally named "Dapper" — Dapper-call detection is gated on
    // the invoked method's ContainingAssembly.Name, precisely so a same-named Query()/Execute() method on
    // an unrelated type can't be mistaken for a Dapper call. That gate is untestable against a single
    // in-source fake (everything in one compilation shares one assembly name), so this builds a second
    // compilation and references it, the same way the real Dapper NuGet package would be referenced.
    private static readonly MetadataReference DapperReference = BuildDapperAssemblyReference();

    private static MetadataReference BuildDapperAssemblyReference()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
            namespace Dapper
            {
                public static class SqlMapper
                {
                    public static System.Collections.Generic.IEnumerable<T> Query<T>(this System.Data.IDbConnection cnn, string sql, object param = null) => null;
                    public static System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<T>> QueryAsync<T>(this System.Data.IDbConnection cnn, string sql, object param = null) => null;
                    public static int Execute(this System.Data.IDbConnection cnn, string sql, object param = null) => 0;
                }
            }
            """);
        CSharpCompilation compilation = CSharpCompilation.Create("Dapper", new[] { tree }, BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(ms);
        if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        ms.Seek(0, SeekOrigin.Begin);

        return MetadataReference.CreateFromImage(ms.ToArray());
    }

    private static RoslynSemanticModel Compile(string source, bool withDapper = false)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(EfFakeFramework + "\n" + source, path: Path.Combine(Path.GetTempPath(), "Test.cs"));
        MetadataReference[] refs = withDapper ? BaseReferences.Append(DapperReference).ToArray() : BaseReferences;
        CSharpCompilation compilation = CSharpCompilation.Create("TestAssembly", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new RoslynSemanticModel(compilation, new[] { tree });
    }

    private static async Task<List<NodeDiscovery>> Analyze(RoslynSemanticModel model)
    {
        var sink = new FakeSink();
        await new DatabaseOperationAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);
        return sink.Nodes;
    }

    [Fact]
    public async Task DbSet_Add_is_classified_as_an_insert_with_the_right_entity()
    {
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    public Microsoft.EntityFrameworkCore.DbSet<Order> Orders { get; set; }
                }
                public class OrderRepository
                {
                    private readonly AppDbContext _db;
                    public OrderRepository(AppDbContext db) { _db = db; }
                    public void Create(Order order) => _db.Orders.Add(order);
                }
            }
            """));

        NodeDiscovery op = Assert.Single(found);
        Assert.Equal("Insert", op.Properties["operation"]);
        Assert.Equal("Order", op.Properties["entity"]);
        Assert.Equal("OrderRepository", op.Properties["owner"]);
        Assert.Equal("Create", op.Properties["callerMethod"]);
    }

    [Fact]
    public async Task Full_LINQ_chain_captures_operators_no_tracking_and_async()
    {
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    public Microsoft.EntityFrameworkCore.DbSet<Order> Orders { get; set; }
                }
                public class OrderRepository
                {
                    private readonly AppDbContext _db;
                    public OrderRepository(AppDbContext db) { _db = db; }
                    public System.Threading.Tasks.Task<System.Collections.Generic.List<Order>> GetOpen() =>
                        _db.Orders.Where(o => true).Include("Items").AsNoTracking().ToListAsync();
                }
            }
            """));

        NodeDiscovery op = Assert.Single(found);
        Assert.Equal("Read", op.Properties["operation"]);
        Assert.Equal("Order", op.Properties["entity"]);
        Assert.Equal("Where, Include, AsNoTracking", op.Properties["operators"]);
        Assert.Equal("no-tracking", op.Properties["tracking"]);
        Assert.Equal("true", op.Properties["async"]);
    }

    [Fact]
    public async Task SaveChangesAsync_on_a_real_DbContext_is_captured_as_Persist()
    {
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext { }
                public class UnitOfWork
                {
                    private readonly AppDbContext _db;
                    public UnitOfWork(AppDbContext db) { _db = db; }
                    public System.Threading.Tasks.Task Commit() => _db.SaveChangesAsync();
                }
            }
            """));

        NodeDiscovery op = Assert.Single(found);
        Assert.Equal("Persist", op.Properties["operation"]);
        Assert.False(op.Properties.ContainsKey("entity"));
    }

    [Fact]
    public async Task List_Add_is_NOT_mistaken_for_a_database_insert()
    {
        // The core precision claim: a plain in-memory List<Order> also carries a generic type argument, so
        // detection must require the receiver to be genuinely DbSet<T>-typed, not just "any generic type".
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { }
                public class OrderBatcher
                {
                    private readonly System.Collections.Generic.List<Order> _pending = new();
                    public void Stage(Order order) => _pending.Add(order);
                }
            }
            """));

        Assert.Empty(found);
    }

    [Fact]
    public async Task SaveChanges_on_an_unrelated_type_is_not_captured()
    {
        // "SaveChanges" is a distinctive enough method name that it's trusted without an entity, but the
        // receiver still has to at least look data-access-shaped — an arbitrary class exposing a same-
        // named method must not be captured.
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class DocumentEditor
                {
                    public void SaveChanges() { }
                }
                public class Autosaver
                {
                    private readonly DocumentEditor _editor;
                    public Autosaver(DocumentEditor editor) { _editor = editor; }
                    public void Save() => _editor.SaveChanges();
                }
            }
            """));

        Assert.Empty(found);
    }

    [Fact]
    public async Task Dapper_query_call_is_captured_only_when_it_resolves_into_the_real_Dapper_assembly()
    {
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                using Dapper;
                public class Order { }
                public class OrderRepository
                {
                    private readonly System.Data.IDbConnection _connection;
                    public OrderRepository(System.Data.IDbConnection connection) { _connection = connection; }
                    public System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<Order>> GetAll() =>
                        _connection.QueryAsync<Order>("SELECT * FROM Orders");
                }
            }
            """, withDapper: true));

        NodeDiscovery op = Assert.Single(found);
        Assert.Equal("Read", op.Properties["operation"]);
        Assert.Equal("Dapper", op.Properties["approach"]);
        Assert.Equal("Order", op.Properties["entity"]);
        Assert.Equal("\"SELECT * FROM Orders\"", op.Properties["sql"]);
    }

    [Fact]
    public async Task A_lookalike_Query_method_not_from_the_Dapper_assembly_is_not_captured()
    {
        // Same call shape (.QueryAsync<T>(string) on a field) but the method is defined by this project's
        // own code, not the Dapper package — must not be captured as a database call.
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { }
                public class SearchIndexClient
                {
                    public System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<T>> QueryAsync<T>(string query) => null;
                }
                public class SearchService
                {
                    private readonly SearchIndexClient _client;
                    public SearchService(SearchIndexClient client) { _client = client; }
                    public System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<Order>> Search() =>
                        _client.QueryAsync<Order>("orders");
                }
            }
            """, withDapper: true));

        Assert.Empty(found);
    }

    [Fact]
    public async Task Stored_procedure_shaped_raw_SQL_is_classified_distinctly_from_ordinary_raw_SQL()
    {
        List<NodeDiscovery> found = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    public Microsoft.EntityFrameworkCore.DbSet<Order> Orders { get; set; }
                }
                public class OrderRepository
                {
                    private readonly AppDbContext _db;
                    public OrderRepository(AppDbContext db) { _db = db; }
                    public System.Threading.Tasks.Task<System.Collections.Generic.List<Order>> GetViaProc() =>
                        _db.Orders.FromSqlRaw("EXEC dbo.GetOpenOrders").ToListAsync();
                }
            }
            """));

        // Two true facts, not a duplicate: FromSqlRaw is its own captured call site (the raw SQL text
        // lives here), and the trailing .ToListAsync() that materializes it is a second, separate Read.
        NodeDiscovery raw = Assert.Single(found, n => n.Properties["method"] == "FromSqlRaw");
        Assert.Equal("StoredProcedure", raw.Properties["operation"]);
        Assert.Equal("\"EXEC dbo.GetOpenOrders\"", raw.Properties["sql"]);
        Assert.Equal(2, found.Count);
    }
}
