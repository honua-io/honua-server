// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point that contributes a feature output format out-of-tree (issue #2856,
/// ADR-0066). A format declares a stable wire token (<see cref="FormatId"/>), the media type and
/// file extension it produces, and streams a sequence of canonical <see cref="Feature"/> values to
/// an output <see cref="System.IO.Stream"/>. The host consults registered formats at its
/// export/format-negotiation seam after the built-in formats, so a third party can add, for
/// example, a GeoJSON-Text-Sequence or TSV writer without patching core.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately ASP.NET-free and streaming-first so the public plugin SDK package
/// stays dependency-minimal and Native-AOT compatible: a writer receives an
/// <see cref="IAsyncEnumerable{Feature}"/> and writes bytes to a <see cref="System.IO.Stream"/>; the
/// host owns content negotiation, HTTP headers (content type, content disposition), telemetry, and
/// authorization. Geometry arrives as WKB in <see cref="Feature.Geometry"/>; the writer decides how
/// (or whether) to encode it.
/// </para>
/// <para>
/// Implementations are resolved as singletons and must be thread-safe and side-effect-free.
/// Contributing an output format requires the <see cref="PluginCapability.OutputFormats"/>
/// capability on the plugin's <c>[Plugin]</c> manifest; the host refuses to register a format from a
/// plugin that has not declared it. The whole feature is Enterprise-gated (<c>plugin.sdk</c>) and
/// honors the operator kill-switch.
/// </para>
/// </remarks>
public interface IFeatureOutputFormat
{
    /// <summary>
    /// Gets the stable, lower-case wire token that selects this format (for example
    /// <c>"geojsonl"</c>). Matched case-insensitively against the requested format; must be unique
    /// across registered plugin formats and must not collide with a built-in format token.
    /// </summary>
    string FormatId { get; }

    /// <summary>
    /// Gets the media (MIME) type the host sets on the response, for example
    /// <c>"application/geo+json-seq"</c>.
    /// </summary>
    string MediaType { get; }

    /// <summary>
    /// Gets the file extension (without a leading dot) the host uses for the download filename, for
    /// example <c>"geojsonl"</c>.
    /// </summary>
    string FileExtension { get; }

    /// <summary>
    /// Serializes the supplied features to <paramref name="output"/> in this format.
    /// </summary>
    /// <param name="features">The features to write, streamed to keep memory bounded.</param>
    /// <param name="context">Layer/service identity, projected field schema, and output SRID.</param>
    /// <param name="output">The destination stream; the host owns its lifetime and does not expect it closed.</param>
    /// <param name="cancellationToken">Cancellation token tied to the request lifetime.</param>
    /// <returns>The number of features written, used by the host for telemetry and logging.</returns>
    ValueTask<long> WriteAsync(
        IAsyncEnumerable<Feature> features,
        FeatureOutputFormatContext context,
        Stream output,
        CancellationToken cancellationToken);
}
