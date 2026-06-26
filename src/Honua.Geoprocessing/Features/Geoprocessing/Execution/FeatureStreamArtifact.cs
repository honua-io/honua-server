// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Streaming in/out contract for the GeoETL transform, source, and sink executors.
/// Replaces the whole-collection base64 data-URI (<see cref="FeatureCollectionArtifact"/>)
/// for large feature sets: a step reads features incrementally from an upstream
/// artifact and writes them incrementally to a spillable backing store, so the full
/// set is never materialized in memory and the hard 50 MiB artifact-cap failure no
/// longer applies to streamable transforms.
///
/// <para>
/// Two artifact shapes flow through the DAG, both opaque strings that the workflow
/// runtime stores verbatim and projects into the downstream step's <c>input</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Inline (back-compat fast path)</b> — a
///     <c>data:application/geo+json;base64,&lt;...&gt;</c> data URI carrying the whole
///     FeatureCollection. Emitted for small outputs so tiny pipelines keep their
///     existing artifact bytes and downstream consumers (OGC API Processes surfaces)
///     see the same document.
///   </description></item>
///   <item><description>
///     <b>Spill (streamed)</b> — a <c>honua-feature-stream:v1;ndjson;path=&lt;abs&gt;;count=&lt;n&gt;;bytes=&lt;n&gt;</c>
///     reference pointing at a newline-delimited GeoJSON (NDJSON) file under the
///     executor output root. One GeoJSON <c>Feature</c> per line, read and written a
///     line at a time so memory stays bounded regardless of feature count.
///   </description></item>
/// </list>
///
/// <para>
/// Reads accept either shape so a streaming step can consume the output of a
/// pre-existing inline producer and vice versa. Uses the managed NetTopologySuite
/// GeoJSON reader/writer per feature — no native dependency, AOT-safe.
/// </para>
/// </summary>
internal static class FeatureStreamArtifact
{
    /// <summary>
    /// Reference scheme prefix for a spilled NDJSON feature stream. The remainder is a
    /// <c>;</c>-delimited key/value list (<c>ndjson;path=...;count=...;bytes=...</c>).
    /// </summary>
    public const string StreamReferencePrefix = "honua-feature-stream:v1;";

    /// <summary>
    /// Foreign-member key used to carry a feature's geometry SRID through the NDJSON line.
    /// GeoJSON has no native SRID, and the NTS reader/writer resets it to the factory default
    /// on round-trip, so reprojected geometries would otherwise lose their target SRID across
    /// the spill boundary. We stash it as a reserved property on write and restore it (then
    /// strip the property) on read, so streaming preserves SRID fidelity end-to-end.
    /// </summary>
    private const string SridMemberKey = "__honua_srid";

    /// <summary>
    /// Default ceiling, in decoded bytes, under which an output is published inline as
    /// a base64 FeatureCollection data URI (back-compat) instead of spilling to a file.
    /// Outputs at or above this size spill, so small pipelines keep their existing
    /// artifact shape while large ones stream. 1 MiB keeps inline artifacts comfortably
    /// under the legacy 50 MiB cap with headroom for base64 expansion.
    /// </summary>
    public const long DefaultInlineThresholdBytes = 1L * 1024L * 1024L;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="reference"/> is a spilled NDJSON stream
    /// reference rather than an inline data URI.
    /// </summary>
    public static bool IsStreamReference(string? reference)
        => reference is not null && reference.StartsWith(StreamReferencePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Opens an incremental feature stream over an upstream artifact reference. Accepts
    /// both the spilled NDJSON reference and the inline base64 FeatureCollection data
    /// URI, so a streaming step transparently consumes either producer. The returned
    /// sequence is lazy: features are decoded one at a time as the consumer pulls them.
    /// </summary>
    /// <param name="reference">The upstream artifact reference (stream ref or data URI).</param>
    /// <param name="error">A classified error when the reference cannot be opened.</param>
    /// <param name="stream">The lazy feature sequence on success.</param>
    /// <param name="maxInlineDecodedBytes">
    /// Ceiling applied only to inline data-URI inputs (mirrors
    /// <see cref="FeatureCollectionArtifact.TryParseDataUri"/>). Spilled streams are
    /// unbounded — that is the whole point — so the ceiling does not apply to them.
    /// </param>
    public static bool TryOpenRead(
        string? reference,
        out string error,
        out IAsyncEnumerable<IFeature> stream,
        long maxInlineDecodedBytes = long.MaxValue)
    {
        error = "";
        stream = AsyncEnumerable.Empty<IFeature>();

        if (string.IsNullOrWhiteSpace(reference))
        {
            error = "value is missing or empty";
            return false;
        }

        if (IsStreamReference(reference))
        {
            if (!TryParseStreamReference(reference, out var descriptor, out error))
            {
                return false;
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

    /// <summary>
    /// Parses a spilled NDJSON stream reference into its descriptor fields.
    /// </summary>
    public static bool TryParseStreamReference(
        string reference,
        out StreamDescriptor descriptor,
        out string error)
    {
        descriptor = default;
        error = "";

        if (!IsStreamReference(reference))
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

    /// <summary>
    /// Builds a spilled NDJSON stream reference for a backing file. The path is URI-escaped
    /// so it survives the <c>;</c>-delimited reference grammar even on Windows.
    /// </summary>
    public static string BuildStreamReference(string path, long count, long bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        var escaped = Uri.EscapeDataString(path);
        return $"{StreamReferencePrefix}ndjson;path={escaped};count={count.ToString(CultureInfo.InvariantCulture)};bytes={bytes.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Allocates a fresh, collision-free backing file path under the executor output
    /// root for a spilled stream. The caller owns the file lifetime.
    /// </summary>
    public static string AllocateSpillPath(string outputRootDirectory, string operationId, string processId)
    {
        ArgumentNullException.ThrowIfNull(outputRootDirectory);

        var streamRoot = Path.Combine(outputRootDirectory, "streams");
        Directory.CreateDirectory(streamRoot);

        var safeProcess = SanitizeForFileName(processId);
        var safeOperation = SanitizeForFileName(operationId);
        var unique = Guid.NewGuid().ToString("N");
        return Path.Combine(streamRoot, $"{safeOperation}.{safeProcess}.{unique}.ndjson");
    }

    /// <summary>
    /// Streams a sequence of features to an NDJSON backing file one line at a time, then
    /// returns a spill reference describing it. Memory stays bounded: a single feature is
    /// serialized at a time and the writer flushes as the buffer fills.
    /// </summary>
    public static async Task<string> WriteStreamAsync(
        string path,
        IAsyncEnumerable<IFeature> features,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(features);

        var writer = new GeoJsonWriter();
        long count = 0;
        long bytes = 0;

        await using (var fileStream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true))
        await using (var textWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = SerializeLine(writer, feature);
                await textWriter.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                count++;
                // '\n' line terminator is one byte; the line itself is single-byte-per-char
                // for ASCII but may be multi-byte for UTF-8 — Encoding.UTF8 gives the true count.
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
            }

            await textWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return BuildStreamReference(path, count, bytes);
    }

    /// <summary>
    /// Serializes one feature to an NDJSON line, stamping its geometry SRID as a reserved
    /// foreign member so the spill round-trip preserves SRID fidelity (GeoJSON otherwise
    /// drops it). Shared by the bulk writer and the publisher's incremental spill writer.
    /// </summary>
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

    /// <summary>
    /// Parses one NDJSON line back to a feature, restoring the geometry SRID stashed by
    /// <see cref="SerializeLine"/> and stripping the reserved member so it never leaks
    /// downstream.
    /// </summary>
    public static IFeature? DeserializeLine(GeoJsonReader reader, string line)
    {
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
        }

        var rebuilt = new AttributesTable();
        foreach (var name in attributes.GetNames())
        {
            if (!string.Equals(name, SridMemberKey, StringComparison.Ordinal))
            {
                rebuilt.Add(name, attributes.GetOptionalValue(name));
            }
        }

        return new Feature(feature.Geometry, rebuilt);
    }

    /// <summary>
    /// Reads an NDJSON backing file as a lazy feature sequence, one line (one feature) at
    /// a time. Memory stays bounded by the largest single feature, never the whole file.
    /// </summary>
    public static async IAsyncEnumerable<IFeature> ReadNdjsonFileAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var reader = new GeoJsonReader();
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

    /// <summary>
    /// Describes a spilled NDJSON feature stream: the backing file path and the
    /// feature/byte counts recorded when it was written.
    /// </summary>
    internal readonly record struct StreamDescriptor(string Path, long Count, long Bytes);
}

/// <summary>
/// Minimal async-enumerable helpers so the streaming artifact contract does not pull in
/// System.Linq.Async. Keeps the dependency surface AOT-friendly.
/// </summary>
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
