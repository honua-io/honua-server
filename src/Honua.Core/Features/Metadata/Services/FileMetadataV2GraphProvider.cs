// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Metadata.Services;

/// <summary>
/// File-backed <see cref="IMetadataV2GraphProvider"/>. Loads a single graph JSON document
/// on first access and caches the snapshot for subsequent reads. Intended for development,
/// tests, and CI fixtures. Production should use a database-backed store.
/// </summary>
public sealed class FileMetadataV2GraphProvider : IMetadataV2GraphProvider, IDisposable
{
    private readonly string _path;
    private readonly ILogger<FileMetadataV2GraphProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MetadataV2GraphSnapshot? _snapshot;

    public FileMetadataV2GraphProvider(string path, ILogger<FileMetadataV2GraphProvider> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            _snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return current.Graph.Revision == revision ? current : null;
    }

    /// <summary>
    /// Forces a reload from disk on the next call.
    /// </summary>
    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _snapshot = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MetadataV2GraphSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException($"Metadata v2 graph file not found at '{_path}'.", _path);
        }

        await using var stream = File.OpenRead(_path);
        var graph = await JsonSerializer.DeserializeAsync(
            stream,
            MetadataV2JsonContext.Default.MetadataV2Graph,
            cancellationToken).ConfigureAwait(false);

        if (graph is null)
        {
            throw new InvalidDataException($"Metadata v2 graph at '{_path}' is empty.");
        }

        var validation = MetadataV2GraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            var joinedErrors = string.Join("; ", validation.Errors);
            FileMetadataV2GraphProviderLog.GraphValidationFailed(_logger, _path, joinedErrors);
            throw new InvalidDataException(
                $"Metadata v2 graph at '{_path}' failed validation: {joinedErrors}");
        }

        var etag = ComputeEtag(_path);
        FileMetadataV2GraphProviderLog.GraphLoaded(
            _logger,
            _path,
            graph.Revision,
            graph.Environment,
            graph.Resources.Count,
            graph.Services.Count,
            graph.Publications.Count);

        return new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
    }

    private static string ComputeEtag(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
