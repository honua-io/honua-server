// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Simple file-backed implementation of <see cref="IMigrationRunCheckpointStore"/>
/// that persists each run's checkpoint as a JSON file under the configured root
/// directory. Designed for single-process migration runs (development, tests) and
/// as a reference implementation for richer persistence backends.
/// </summary>
public sealed class FileSystemMigrationRunCheckpointStore : IMigrationRunCheckpointStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootDirectory;

    /// <summary>
    /// Initializes a new <see cref="FileSystemMigrationRunCheckpointStore"/>.
    /// </summary>
    /// <param name="rootDirectory">Directory under which per-run checkpoint files are stored.</param>
    public FileSystemMigrationRunCheckpointStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>Root directory used to persist checkpoint files.</summary>
    public string RootDirectory => _rootDirectory;

    /// <inheritdoc />
    public async ValueTask SaveAsync(MigrationRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var sanitized = MigrationRunCheckpointSanitizer.Sanitize(checkpoint);
        var path = PathFor(sanitized.RunId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(
                sanitized,
                MigrationRunCheckpointJsonContext.Default.MigrationRunCheckpoint);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MigrationRunCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var safeRunId = SafePathSegment(runId);
        var path = PathFor(safeRunId);
        if (!File.Exists(path)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var checkpoint = JsonSerializer.Deserialize(
                json,
                MigrationRunCheckpointJsonContext.Default.MigrationRunCheckpoint);
            return checkpoint is null ? null : MigrationRunCheckpointSanitizer.Sanitize(checkpoint);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var safeRunId = SafePathSegment(runId);
        var path = PathFor(safeRunId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private string PathFor(string runId) => Path.Combine(_rootDirectory, $"{SafePathSegment(runId)}.json");

    private static string SafePathSegment(string runId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[runId.Length];
        for (var i = 0; i < runId.Length; i++)
        {
            var character = runId[i];
            buffer[i] = Array.IndexOf(invalid, character) >= 0 ? '_' : character;
        }

        var sanitized = new string(buffer);
        // Disallow path traversal segments.
        return sanitized.Replace("..", "__", StringComparison.Ordinal);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MigrationRunCheckpoint))]
internal sealed partial class MigrationRunCheckpointJsonContext : JsonSerializerContext
{
}
