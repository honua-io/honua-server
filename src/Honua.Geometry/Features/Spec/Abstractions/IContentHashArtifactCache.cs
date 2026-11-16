// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

public interface IContentHashArtifactCache
{
    Task<CachedArtifactRef?> TryGetAsync(string contentHash, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default);

    Task<CachedArtifactRef> PutAsync(SpecArtifactPayload payload, CancellationToken cancellationToken = default);
}
