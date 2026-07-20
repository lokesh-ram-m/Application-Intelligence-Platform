using System.Text;

using Aip.Abstractions.Documents;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Microsoft.Extensions.DependencyInjection;

namespace Aip.Infrastructure.AzureBlob;

/// <summary>
/// The Azure Blob Storage <see cref="IDocumentStore"/> — for the standalone/production, multi-repo
/// scenario, where a central, durable store is needed that no analysis run's process lifetime owns. Blob name is
/// <c>&lt;application-slug&gt;/&lt;relativePath&gt;</c>, mirroring <see cref="FileSystemDocumentStore"/>'s
/// layout exactly, so the two implementations are interchangeable without callers noticing.
/// </summary>
public sealed class AzureBlobDocumentStore : IDocumentStore
{
    private readonly BlobContainerClient _container;

    public AzureBlobDocumentStore(string connectionString, string containerName = "documents")
    {
        _container = new BlobContainerClient(connectionString, containerName);
        _container.CreateIfNotExists();
    }

    public async Task WriteAsync(string application, string relativePath, string content, string contentType = "text/markdown", CancellationToken ct = default)
    {
        BlobClient blob = _container.GetBlobClient(BlobName(application, relativePath));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<string?> ReadAsync(string application, string relativePath, CancellationToken ct = default)
    {
        BlobClient blob = _container.GetBlobClient(BlobName(application, relativePath));
        if (!await blob.ExistsAsync(ct)) return null;
        BlobDownloadResult result = await blob.DownloadContentAsync(ct);

        return result.Content.ToString();
    }

    public async Task<IReadOnlyList<string>> ListAsync(string application, CancellationToken ct = default)
    {
        string prefix = DocumentPaths.SlugifyApplication(application) + "/";
        var paths = new List<string>();
        await foreach (BlobItem item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            paths.Add(item.Name[prefix.Length..]);

        return paths;
    }

    public async Task ClearApplicationAsync(string application, CancellationToken ct = default)
    {
        string prefix = DocumentPaths.SlugifyApplication(application) + "/";
        await foreach (BlobItem item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            await _container.DeleteBlobIfExistsAsync(item.Name, cancellationToken: ct);
    }

    private static string BlobName(string application, string relativePath) =>
        $"{DocumentPaths.SlugifyApplication(application)}/{DocumentPaths.NormalizeRelativePath(relativePath)}";
}

public static class AzureBlobDocumentStoreModule
{
    /// <summary>Registers <see cref="AzureBlobDocumentStore"/> as the active <see cref="IDocumentStore"/>,
    /// overriding whatever <c>Aip.Infrastructure</c> registered by default.</summary>
    public static IServiceCollection AddAipAzureBlobDocumentStore(this IServiceCollection services, string connectionString, string containerName = "documents")
    {
        services.AddSingleton<IDocumentStore>(_ => new AzureBlobDocumentStore(connectionString, containerName));

        return services;
    }
}
