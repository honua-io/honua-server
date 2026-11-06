// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

// A checkpoint written here cannot resume on another node; production must use the shared store.
public sealed class FileSystemMigrationRunCheckpointStore : IMigrationRunCheckpointStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootDirectory;

    public FileSystemMigrationRunCheckpointStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

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

    public void Dispose() => _gate.Dispose();

    private string PathFor(string runId)
    {
        var segment = SafePathSegment(runId);
        // Path.Combine is safe here: SafePathSegment already strips '/', '\', and '..' from
        // `segment`, so it can never look like a rooted path that would discard _rootDirectory.
        // The containment check below is defense-in-depth in case that guarantee is ever weakened.
        var candidate = Path.GetFullPath(Path.Join(_rootDirectory, $"{segment}.json"));
        // Containment check: reject any path that escapes the root directory (symlinks,
        // future encoding tricks, etc. are caught here even if SafePathSegment missed them).
        var root = Path.GetFullPath(_rootDirectory) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path traversal detected: the resolved path escapes the checkpoint root directory.");
        }

        return candidate;
    }

    private static string SafePathSegment(string runId)
    {
        // Explicitly reject path-separator characters in addition to OS-reported invalid
        // file-name characters. On Linux, Path.GetInvalidFileNameChars() only contains
        // '\0', so '/' would pass through unchanged and Path.Combine would then discard
        // the root because the second argument looks absolute.
        Span<char> buffer = stackalloc char[runId.Length];
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < runId.Length; i++)
        {
            var character = runId[i];
            buffer[i] = (Array.IndexOf(invalid, character) >= 0 || character == '/' || character == '\\')
                ? '_'
                : character;
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
public sealed partial class MigrationRunCheckpointJsonContext : JsonSerializerContext
{
}
