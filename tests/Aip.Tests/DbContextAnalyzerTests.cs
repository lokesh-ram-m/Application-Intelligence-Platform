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
/// Exercises DbContextAnalyzer's Fluent API parsing: relationship cardinality (the HasOne/HasMany +
/// WithOne/WithMany combination EF Core actually uses to distinguish one-to-one from many-to-one, and
/// one-to-many from many-to-many) and the broader entity-level vocabulary (ToTable/HasKey/HasIndex).
/// </summary>
public class DbContextAnalyzerTests
{
    // A minimal fake of EF Core's ModelBuilder fluent surface — just enough generic shape for the real
    // analyzer code to walk (forward through .WithOne()/.WithMany()/.HasForeignKey(), backward through
    // .HasOne()/.HasMany() to the modelBuilder.Entity<T>() root) without depending on the real package.
    private const string EfFakeFramework = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { }
            public class DbSet<T> { }
            public class ModelBuilder
            {
                public EntityTypeBuilder<T> Entity<T>() where T : class => null;
            }
            public class EntityTypeBuilder<T> where T : class
            {
                public EntityTypeBuilder<T> ToTable(string name) => this;
                public EntityTypeBuilder<T> HasKey(System.Func<T, object> keyExpression) => this;
                public EntityTypeBuilder<T> HasKey(string propertyName) => this;
                public EntityTypeBuilder<T> HasIndex(System.Func<T, object> indexExpression) => this;
                public ReferenceNavigationBuilder<T, TRelated> HasOne<TRelated>(System.Func<T, TRelated> navigation = null) where TRelated : class => null;
                public CollectionNavigationBuilder<T, TRelated> HasMany<TRelated>(System.Func<T, object> navigation = null) where TRelated : class => null;
            }
            public class ReferenceNavigationBuilder<T, TRelated> where T : class where TRelated : class
            {
                public ReferenceCollectionBuilder<TRelated, T> WithMany() => null;
                public ReferenceReferenceBuilder<T, TRelated> WithOne() => null;
            }
            public class CollectionNavigationBuilder<T, TRelated> where T : class where TRelated : class
            {
                public ReferenceCollectionBuilder<T, TRelated> WithOne() => null;
                public CollectionCollectionBuilder<T, TRelated> WithMany() => null;
            }
            public class ReferenceCollectionBuilder<T, TRelated> where T : class where TRelated : class
            {
                public ReferenceCollectionBuilder<T, TRelated> HasForeignKey(System.Func<TRelated, object> fk) => this;
            }
            public class ReferenceReferenceBuilder<T, TRelated> { }
            public class CollectionCollectionBuilder<T, TRelated> { }
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
        SyntaxTree tree = CSharpSyntaxTree.ParseText(EfFakeFramework + "\n" + source, path: Path.Combine(Path.GetTempPath(), "Test.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("TestAssembly", new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new RoslynSemanticModel(compilation, new[] { tree });
    }

    private static async Task<FakeSink> Analyze(RoslynSemanticModel model)
    {
        var sink = new FakeSink();
        await new DbContextAnalyzer().AnalyzeAsync(new FakeContext(model, "Api"), sink);
        return sink;
    }

    [Fact]
    public async Task HasOne_followed_by_WithOne_is_a_one_to_one_relationship()
    {
        FakeSink sink = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { public OrderInvoice Invoice { get; set; } }
                public class OrderInvoice { public Order Order { get; set; } }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    protected void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Order>().HasOne(o => o.Invoice).WithOne();
                    }
                }
            }
            """));

        Assert.Contains(sink.Relationships, r => r.Type.Value == "HAS_ONE");
        Assert.DoesNotContain(sink.Relationships, r => r.Type.Value == "REFERENCES");
    }

    [Fact]
    public async Task HasOne_followed_by_WithMany_is_the_conventional_many_to_one_REFERENCES()
    {
        FakeSink sink = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { public Customer Customer { get; set; } }
                public class Customer { }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    protected void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Order>().HasOne(o => o.Customer).WithMany();
                    }
                }
            }
            """));

        Assert.Contains(sink.Relationships, r => r.Type.Value == "REFERENCES");
        Assert.DoesNotContain(sink.Relationships, r => r.Type.Value == "HAS_ONE");
    }

    [Fact]
    public async Task HasMany_followed_by_WithMany_is_many_to_many()
    {
        FakeSink sink = await Analyze(Compile("""
            namespace TestApp
            {
                public class Student { }
                public class Course { }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    protected void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Student>().HasMany<Course>().WithMany();
                    }
                }
            }
            """));

        Assert.Contains(sink.Relationships, r => r.Type.Value == "MANY_TO_MANY");
        Assert.DoesNotContain(sink.Relationships, r => r.Type.Value == "HAS_MANY");
    }

    [Fact]
    public async Task ToTable_HasKey_and_HasIndex_are_captured_as_entity_properties()
    {
        FakeSink sink = await Analyze(Compile("""
            namespace TestApp
            {
                public class Order { public string Code { get; set; } public string Email { get; set; } }
                public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    protected void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Order>().ToTable("Orders").HasKey(o => o.Code);
                        modelBuilder.Entity<Order>().HasIndex(o => o.Email);
                    }
                }
            }
            """));

        NodeDiscovery order = sink.Nodes.Single(n => n.Kind.Value == "Entity" && n.Properties.ContainsKey("tableName"));
        Assert.Equal("Orders", order.Properties["tableName"]);

        Assert.Contains(sink.Nodes, n => n.Kind.Value == "Entity" && n.Properties.GetValueOrDefault("primaryKey") == "Code");
        Assert.Contains(sink.Nodes, n => n.Kind.Value == "Entity" && n.Properties.GetValueOrDefault("indexedProperties") == "Email");
    }
}
