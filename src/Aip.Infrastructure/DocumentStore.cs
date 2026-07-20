using Aip.Abstractions.Documents;

namespace Aip.Infrastructure;

/// <summary>
/// The default <see cref="IDocumentStore"/> — writes documents under a local folder
/// (<c>&lt;root&gt;/&lt;application-slug&gt;/&lt;relativePath&gt;</c>). Used for local/dev runs; the
/// standalone/production, multi-repo scenario swaps in <c>AzureBlobDocumentStore</c> (see
/// Aip.Infrastructure.AzureBlob) instead — callers never change, since both sit behind the same port.
/// </summary>
public sealed class FileSystemDocumentStore : IDocumentStore
{
    private readonly string _root;

    public FileSystemDocumentStore(string? root = null) =>
        _root = root
            ?? Environment.GetEnvironmentVariable("AIP_DOCS_ROOT")
            ?? Path.Combine(Environment.GetEnvironmentVariable("AIP_OUTPUT") ?? Path.Combine(Directory.GetCurrentDirectory(), "output"), "documents");

    public Task WriteAsync(string application, string relativePath, string content, string contentType = "text/markdown", CancellationToken ct = default)
    {
        string path = PathFor(application, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        return File.WriteAllTextAsync(path, content, ct);
    }

    public async Task<string?> ReadAsync(string application, string relativePath, CancellationToken ct = default)
    {
        string path = PathFor(application, relativePath);

        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public Task<IReadOnlyList<string>> ListAsync(string application, CancellationToken ct = default)
    {
        string dir = AppDir(application);
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        IReadOnlyList<string> paths = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .ToList();

        return Task.FromResult(paths);
    }

    public Task ClearApplicationAsync(string application, CancellationToken ct = default)
    {
        string dir = AppDir(application);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }

    private string AppDir(string application) => Path.Combine(_root, DocumentPaths.SlugifyApplication(application));

    private string PathFor(string application, string relativePath) =>
        Path.Combine(AppDir(application), DocumentPaths.NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar));
}
