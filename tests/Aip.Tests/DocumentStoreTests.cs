using Aip.Abstractions.Documents;
using Aip.Infrastructure;

using Xunit;

namespace Aip.Tests;

public class DocumentPathsTests
{
    [Theory]
    [InlineData("ShopApp", "shopapp")]
    [InlineData("Entity Management!", "entity-management-")]
    public void SlugifyApplication_lowercases_and_replaces_non_alphanumerics(string input, string expected) =>
        Assert.Equal(expected, DocumentPaths.SlugifyApplication(input));

    [Theory]
    [InlineData(@"product-specification\overview.md", "product-specification/overview.md")]
    [InlineData("/leading/slash.md", "leading/slash.md")]
    public void NormalizeRelativePath_normalizes_separators(string input, string expected) =>
        Assert.Equal(expected, DocumentPaths.NormalizeRelativePath(input));

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("a/../../b.md")]
    [InlineData(".")]
    public void NormalizeRelativePath_rejects_traversal(string input) =>
        Assert.Throws<ArgumentException>(() => DocumentPaths.NormalizeRelativePath(input));
}

public class FileSystemDocumentStoreTests
{
    private static FileSystemDocumentStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "aip-docstore-test-" + Guid.NewGuid().ToString("N"));

        return new FileSystemDocumentStore(root);
    }

    [Fact]
    public async Task Write_then_read_round_trips_content()
    {
        var store = NewStore(out string root);
        try
        {
            await store.WriteAsync("ShopApp", "product-specification/overview.md", "# Overview");
            string? content = await store.ReadAsync("ShopApp", "product-specification/overview.md");
            Assert.Equal("# Overview", content);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Read_of_missing_document_returns_null()
    {
        var store = NewStore(out string root);
        try { Assert.Null(await store.ReadAsync("ShopApp", "no-such-page.md")); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task List_returns_every_written_path_for_the_application_only()
    {
        var store = NewStore(out string root);
        try
        {
            await store.WriteAsync("ShopApp", "a.md", "A");
            await store.WriteAsync("ShopApp", "nested/b.md", "B");
            await store.WriteAsync("OtherApp", "c.md", "C");

            IReadOnlyList<string> shopAppDocs = await store.ListAsync("ShopApp");

            Assert.Equal(new[] { "a.md", "nested/b.md" }, shopAppDocs.OrderBy(p => p));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ClearApplication_removes_only_that_applications_documents()
    {
        var store = NewStore(out string root);
        try
        {
            await store.WriteAsync("ShopApp", "a.md", "A");
            await store.WriteAsync("OtherApp", "b.md", "B");

            await store.ClearApplicationAsync("ShopApp");

            Assert.Empty(await store.ListAsync("ShopApp"));
            Assert.NotEmpty(await store.ListAsync("OtherApp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Write_overwrites_existing_content_at_the_same_path()
    {
        var store = NewStore(out string root);
        try
        {
            await store.WriteAsync("ShopApp", "a.md", "old");
            await store.WriteAsync("ShopApp", "a.md", "new");
            Assert.Equal("new", await store.ReadAsync("ShopApp", "a.md"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
