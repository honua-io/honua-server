// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Abstractions.Features.Rasters.Abstractions;

public sealed record CogArtifactReference(string Href, string ContentType, long? SizeBytes);

public interface ICogArtifactLocator
{
    ValueTask<CogArtifactReference?> TryResolveAsync(int layerId, CancellationToken cancellationToken);
}
