// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Abstractions.Features.Rasters.Abstractions;

// QGIS loads a STAC asset only when the media type carries profile=cloud-optimized, so a plain image/tiff advertises the raster and still leaves it unusable.
public sealed record CogArtifactReference(string Href, string ContentType, long? SizeBytes);

// Protocol assemblies advertise a published artifact through this locator because the publisher lives in Honua.Server, which references those assemblies and not the other way round.
public interface ICogArtifactLocator
{
    ValueTask<CogArtifactReference?> TryResolveAsync(int layerId, CancellationToken cancellationToken);
}
