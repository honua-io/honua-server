// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

internal static class FeatureStreamArtifact
{
    public const string StreamReferencePrefix = "honua-feature-stream:v1;";

    private const string SridMemberKey = "__honua_srid";

    public const long DefaultInlineThresholdBytes = 1L * 1024L * 1024L;

    public static bool IsStreamReference(string? reference)
        => reference is not null && (reference.StartsWith(StreamReferencePrefix, StringComparison.Ordinal)
            || reference.StartsWith(DurableFeatureStream.Prefix, StringComparison.Ordinal));

    // A spilled path must stay under the executor output root so a caller cannot point the reference at an arbitrary file.
    public static bool TryOpenRead(
        string? reference,
        out string error,
        out IAsyncEnumerable<IFeature> stream,
        long maxInlineDecodedBytes = long.MaxValue,
        string? outputRootDirectory = null,
        IJobExecutionContext? executionContext = null)
    {
        error = "";
        stream = AsyncEnumerable.Empty<IFeature>();

        if (string.IsNullOrWhiteSpace(reference))
        {
            error = "value is missing or empty";
            return false;
        }

        if (reference.StartsWith(DurableFeatureStream.Prefix, StringComparison.Ordinal))
        {
            if (executionContext is not ManagedFeatureStreamContext { Store: not null, Jobs: not null } durable
                || !DurableFeatureStream.TryParse(reference, out _))
            {
                error = "shared feature stream storage and trusted execution identity are required";
                return false;
            }

            stream = DurableFeatureStream.ReadAsync(reference, durable.Job, durable.Jobs, durable.Store, durable.Options, workspaceScopes: durable.WorkspaceScopes);
            return true;
        }

        if (IsStreamReference(reference))
        {
            if (executionContext is ManagedFeatureStreamContext)
            {
                error = "node-local feature stream references are not accepted by managed job execution";
                return false;
            }

            if (!TryParseStreamReference(reference, out var descriptor, out error))
            {
                return false;
            }

            // Guard against path-traversal: reject any stream reference whose backing
            // file path falls outside the configured output root directory. Legitimate
            // spilled streams are always written under OutputRootDirectory/streams/ by
            // AllocateSpillPath, so a path outside the root is always an injection attempt.
            if (outputRootDirectory is not null)
            {
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                var root = Path.GetFullPath(outputRootDirectory);
                var candidate = Path.GetFullPath(descriptor.Path);
                var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;

                if (!candidate.StartsWith(rootPrefix, comparison))
                {
                    error = "stream reference path is outside the output root";
                    return false;
                }
            }

            if (!File.Exists(descriptor.Path))
            {
                error = "feature stream backing file no longer exists";
                return false;
            }

            stream = ReadNdjsonFileAsync(descriptor.Path);
            return true;
        }

        // Inline back-compat path: decode the whole FeatureCollection up front (it is
        // bounded by maxInlineDecodedBytes) and yield its features. Small collections
        // never spilled, so this keeps tiny pipelines working unchanged.
        if (!FeatureCollectionArtifact.TryParseDataUri(reference, out var collection, out error, maxInlineDecodedBytes))
        {
            return false;
        }

        stream = ToAsync(collection);
        return true;
    }

    public static bool TryParseStreamReference(
        string reference,
        out StreamDescriptor descriptor,
        out string error)
    {
        descriptor = default;
        error = "";

        if (!reference.StartsWith(StreamReferencePrefix, StringComparison.Ordinal))
        {
            error = $"value must be a '{StreamReferencePrefix}' stream reference";
            return false;
        }

        var body = reference[StreamReferencePrefix.Length..];
        var parts = body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? path = null;
        long count = 0;
        long bytes = 0;
        var format = "";

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                // Bare token is the format marker (e.g. "ndjson").
                format = part;
                continue;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            switch (key)
            {
                case "path":
                    path = Uri.UnescapeDataString(value);
                    break;
                case "count":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
                    break;
                case "bytes":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);
                    break;
                default:
                    break;
            }
        }

        if (!string.Equals(format, "ndjson", StringComparison.Ordinal))
        {
            error = $"unsupported feature stream format '{format}' (expected 'ndjson')";
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "feature stream reference is missing a 'path'";
            return false;
        }

        descriptor = new StreamDescriptor(path!, count, bytes);
        return true;
    }

    public static string BuildStreamReference(string path, long count, long bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        var escaped = Uri.EscapeDataString(path);
        return $"{StreamReferencePrefix}ndjson;path={escaped};count={count.ToString(CultureInfo.InvariantCulture)};bytes={bytes.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string AllocateSpillPath(string outputRootDirectory, string operationId, string processId)
    {
        ArgumentNullException.ThrowIfNull(outputRootDirectory);

        // Second segment is a fixed relative literal, so it can never be
        // rooted and silently discard outputRootDirectory.
        var streamRoot = Path.Join(outputRootDirectory, "streams");
        Directory.CreateDirectory(streamRoot);

        var safeProcess = SanitizeForFileName(processId);
        var safeOperation = SanitizeForFileName(operationId);
        var unique = Guid.NewGuid().ToString("N");
        // safeOperation/safeProcess are already passed through SanitizeForFileName above,
        // which keeps only [A-Za-z0-9-_] (everything else, including '/', '\', and ':',
        // becomes '_'), so the interpolated second segment can never be rooted or contain
        // a path separator.
        return Path.Join(streamRoot, $"{safeOperation}.{safeProcess}.{unique}.ndjson");
    }

    public static async Task<string> WriteStreamAsync(
        string path,
        IAsyncEnumerable<IFeature> features,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(features);

        var writer = GeoJsonArtifactCodec.CreateWriter();
        long count = 0;
        long bytes = 0;

        await using (var fileStream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true))
        {
            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = SerializeLine(writer, feature);
                bytes += await WriteNdjsonLineAsync(fileStream, line, cancellationToken).ConfigureAwait(false);
                count++;
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return BuildStreamReference(path, count, bytes);
    }

    internal static async Task<int> WriteNdjsonLineAsync(FileStream fileStream, string line, CancellationToken cancellationToken)
    {
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(line.Length) + 1;
        var buffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(line, 0, line.Length, buffer, 0);
            buffer[written] = (byte)'\n';
            await fileStream.WriteAsync(buffer.AsMemory(0, written + 1), cancellationToken).ConfigureAwait(false);
            return written + 1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string SerializeLine(GeoJsonWriter writer, IFeature feature)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(feature);

        var srid = feature.Geometry?.SRID ?? 0;
        if (srid == 0)
        {
            return writer.Write(feature);
        }

        // Carry the SRID on a copy of the attributes so the source feature is not mutated.
        var attributes = new AttributesTable();
        if (feature.Attributes is { } source)
        {
            foreach (var name in source.GetNames())
            {
                attributes.Add(name, source.GetOptionalValue(name));
            }
        }

        attributes.Add(SridMemberKey, srid);
        return writer.Write(new Feature(feature.Geometry, attributes));
    }

    // The reserved SRID member is stripped after the geometry is restored so it does not leak downstream.
    public static IFeature? DeserializeLine(GeoJsonReader reader, string line)
        => DeserializeLine(reader, line, out _);

    internal static IFeature? DeserializeLine(GeoJsonReader reader, string line, out bool hasProducerSpatialReference)
    {
        hasProducerSpatialReference = false;
        ArgumentNullException.ThrowIfNull(reader);

        var feature = reader.Read<Feature>(line);
        if (feature is null)
        {
            return null;
        }

        var attributes = feature.Attributes;
        if (attributes is null || !attributes.Exists(SridMemberKey))
        {
            return feature;
        }

        var rawSrid = attributes.GetOptionalValue(SridMemberKey);
        if (feature.Geometry is not null
            && rawSrid is not null
            && int.TryParse(
                Convert.ToString(rawSrid, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var srid))
        {
            feature.Geometry.SRID = srid;
            hasProducerSpatialReference = srid > 0 && rawSrid is int or long;
        }

        var rebuilt = new AttributesTable();
        foreach (var name in attributes.GetNames().Where(name => !string.Equals(name, SridMemberKey, StringComparison.Ordinal)))
        {
            rebuilt.Add(name, attributes.GetOptionalValue(name));
        }

        return new Feature(feature.Geometry, rebuilt);
    }

    public static async IAsyncEnumerable<IFeature> ReadNdjsonFileAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var reader = GeoJsonArtifactCodec.CreateReader();
        await using var fileStream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        using var textReader = new StreamReader(fileStream, Encoding.UTF8);

        while (await textReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var feature = DeserializeLine(reader, line);
            if (feature is not null)
            {
                yield return feature;
            }
        }
    }

    private static async IAsyncEnumerable<IFeature> ToAsync(
        FeatureCollection collection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var feature in collection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static string SanitizeForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "x";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return builder.ToString();
    }

    internal readonly record struct StreamDescriptor(string Path, long Count, long Bytes);
}

internal static class AsyncEnumerable
{
    public static IAsyncEnumerable<T> Empty<T>() => EmptyAsyncEnumerable<T>.Instance;

    private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        public static readonly EmptyAsyncEnumerable<T> Instance = new();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public T Current => default!;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
