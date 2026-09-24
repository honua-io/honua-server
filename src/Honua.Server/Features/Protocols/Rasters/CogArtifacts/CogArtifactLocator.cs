// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Abstractions.Features.Rasters.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Server.Features.Protocols.Rasters.CogArtifacts;

/// <summary>
/// Resolves a layer's published COG so protocol surfaces can advertise it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately reports only what is readable *now*. The object key is derived the
/// same way the publisher writes it, and then confirmed through
/// <see cref="CogArtifactService.ResolvePublishedAsync"/>, which is the identical
/// lookup the public range proxy performs. Deriving the key without that confirmation
/// would let STAC advertise an asset for any layer that merely owns a raster, and a
/// client following it would get a 404 on an href the catalogue promised.
/// </para>
/// <para>
/// An unpublished layer is the ordinary case - every vector layer - so it returns
/// <see langword="null"/> rather than throwing.
/// </para>
/// </remarks>
internal sealed class CogArtifactLocator : ICogArtifactLocator
{
    private readonly CogArtifactService _service;
    private readonly IRasterStore _rasterStore;

    public CogArtifactLocator(CogArtifactService service, IRasterStore rasterStore)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
    }

    public async ValueTask<CogArtifactReference?> TryResolveAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var source = await _service.ResolveSourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (source?.Binding.StorageLayerId is not int storageLayerId)
        {
            return null;
        }

        var primary = await _rasterStore
            .GetPrimaryRasterInfoAsync(storageLayerId, cancellationToken)
            .ConfigureAwait(false);
        if (primary is not { } raster)
        {
            return null;
        }

        var artifactId = CogArtifactService.BuildObjectKey(layerId, raster.Id);
        var target = await _service
            .ResolvePublishedAsync(artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        return new CogArtifactReference(
            CogArtifactService.ProxyRoutePrefix + artifactId,
            CogArtifactService.ContentType,
            target.File.SizeBytes > 0 ? target.File.SizeBytes : null);
    }
}
