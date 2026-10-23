// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Abstractions.Features.Rasters.Abstractions;

/// <summary>
/// A published Cloud Optimized GeoTIFF that a client can read over HTTP range requests.
/// </summary>
/// <param name="Href">Absolute URL of the artifact on the public range proxy.</param>
/// <param name="ContentType">
/// The COG media type. Clients discriminate on the <c>profile=cloud-optimized</c>
/// parameter: QGIS resolves a STAC asset to a loadable layer URI only when it is
/// present, so a plain <c>image/tiff</c> would advertise the asset and still leave it
/// unusable.
/// </param>
/// <param name="SizeBytes">Artifact length when known, for clients that size before reading.</param>
public sealed record CogArtifactReference(string Href, string ContentType, long? SizeBytes);

/// <summary>
/// Resolves the published COG artifact backing a layer, if one exists.
/// </summary>
/// <remarks>
/// This exists to cross an assembly boundary rather than to add behaviour. The COG
/// publisher and its range proxy live in <c>Honua.Server</c>, which references the
/// protocol assemblies and not the other way round, so a protocol that wants to
/// advertise an artifact cannot reach the service that owns it. STAC is the immediate
/// caller: it describes a layer's data and, without this, can only ever offer the
/// GeoJSON representation even when a range-readable raster is published alongside.
/// </remarks>
public interface ICogArtifactLocator
{
    /// <summary>
    /// Returns the published COG for <paramref name="layerId"/>, or <see langword="null"/>
    /// when the layer has no raster or nothing has been published for it.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw for an unpublished layer: callers use this to
    /// decide whether to add an optional asset, and an absent artifact is the ordinary
    /// case for every vector layer.
    /// </remarks>
    ValueTask<CogArtifactReference?> TryResolveAsync(int layerId, CancellationToken cancellationToken);
}
