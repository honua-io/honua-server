// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Abstractions.Features.Rasters.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Server.Features.Protocols.Rasters.CogArtifacts;

// The key is advertised only after the same lookup the public proxy uses, so the catalogue cannot promise an href that is missing.
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
