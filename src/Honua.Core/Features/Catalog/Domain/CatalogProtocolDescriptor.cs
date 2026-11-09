// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

public sealed record CatalogProtocolDescriptor
{
    public required string Protocol { get; init; }
    public required string HrefTemplate { get; init; }
    public bool PreferredLayerRequired { get; init; }
}
