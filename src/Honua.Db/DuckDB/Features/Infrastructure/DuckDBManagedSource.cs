// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Data.Common;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Honua.Core.Features.Security;
using Microsoft.Extensions.Logging;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal sealed class DuckDBManagedSource(
    DuckDBSpatialBootstrap bootstrap,
    ILogger<DuckDBConnectionProvider> connectionLogger,
    IObjectStorageConnectionResolver? resolver = null,
    IImportObjectStoreReader? reader = null) : IDisposable
{
    private const long MaxObjectBytes = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim StagingSlots = new(8, 8);
    private readonly Dictionary<(Guid ConnectionId, string Key), Task<string>> _staged = [];
    private readonly object _gate = new();
    private bool _disposed;

    public DuckDBConnectionProvider CreateConnectionProvider(DuckDBLayerMapping mapping)
        => new("Data Source=:memory:", (connection, ct) => PrepareAsync(connection, mapping, ct), connectionLogger);

    private async Task PrepareAsync(DbConnection connection, DuckDBLayerMapping mapping, CancellationToken cancellationToken)
    {
        if (mapping.ObjectStorageConnectionId is not { } connectionId || mapping.ObjectStorageKey is not { Length: > 0 } key ||
            !mapping.ExternalSourcePaths.IsDefaultOrEmpty ||
            !string.Equals(mapping.ExternalSourceFormat, "GeoParquet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A managed DuckDB source requires one object storage connection and GeoParquet key.");
        }

        Task<string> staged;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staged.TryGetValue((connectionId, key), out staged!))
            {
                staged = StageAsync(connectionId, key, cancellationToken);
                _staged.Add((connectionId, key), staged);
            }
        }

        var path = await staged.WaitAsync(cancellationToken).ConfigureAwait(false);
        var plan = DuckDBExternalSourceSql.BuildExternalSourcePlan(new DuckDBLayerOptions
        {
            Id = mapping.LayerId,
            Table = mapping.TableName,
            ExternalSource = new DuckDBExternalSourceOptions { Path = path },
        }, resolvedPaths: null);
        await bootstrap.PrepareConnectionAsync(connection, [plan], cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> StageAsync(Guid connectionId, string key, CancellationToken cancellationToken)
    {
        if (resolver is null || reader is null)
        {
            throw new InvalidOperationException("Managed DuckDB object storage is unavailable.");
        }

        if (!StagingSlots.Wait(0, cancellationToken))
        {
            throw new InvalidOperationException("DuckDB managed object staging is at capacity.");
        }

        string? path = null;
        try
        {
            var resolution = await resolver.ResolveObjectAsync(connectionId, key, cancellationToken).ConfigureAwait(false);
            if (!resolution.Succeeded)
            {
                throw new InvalidOperationException("DuckDB could not resolve the managed object storage source.");
            }

            var location = ImportObjectLocation.From(resolution);
            var head = await reader.HeadAsync(location, cancellationToken).ConfigureAwait(false);
            if (head.SizeBytes is <= 0 or > MaxObjectBytes || string.IsNullOrEmpty(head.ETag))
            {
                throw new InvalidOperationException("DuckDB managed GeoParquet requires an ETag and an object no larger than 128 MiB.");
            }

            await using var source = await reader.OpenReadAsync(location, head.ETag, cancellationToken).ConfigureAwait(false);
            var candidate = PrivateTempFile.NewPath("honua-duckdb-", ".parquet");
            await using var target = PrivateTempFile.Create(candidate);
            path = candidate;
            await CopyExactAsync(source, target, head.SizeBytes, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            DeleteStagedFile(path);
            throw;
        }
    }

    private static async Task CopyExactAsync(Stream source, Stream target, long remaining, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining + 1, buffer.Length)), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0 && remaining == 0)
                {
                    return;
                }

                if (count == 0 || count > remaining)
                {
                    throw new IOException("DuckDB managed object size changed during download.");
                }

                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var staged in _staged.Values)
            {
                _ = staged.ContinueWith(
                    task =>
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            DeleteStagedFile(task.Result);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private void DeleteStagedFile(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DuckDbLog.ManagedSourceCleanupFailed(connectionLogger, ex);
        }
        finally
        {
            StagingSlots.Release();
        }
    }
}
