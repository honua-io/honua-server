// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Abstractions;

// The metadata write base a publication is checked against: the revision and exact ETag a
// pinned publish must still find when it commits.
public sealed record LayerPublicationBase(long Revision, string? Etag);

public interface ILayerPublicationBaseReader
{
    Task<LayerPublicationBase> ReadPublicationBaseAsync(CancellationToken cancellationToken = default);
}
