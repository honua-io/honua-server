// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

// Capabilities depend only on the source kind, so planning never contacts the remote source.
public sealed record FederatedSourceCapabilities
{
    public required bool SupportsAttributeFilterPushdown { get; init; }

    public required bool SupportsSpatialFilterPushdown { get; init; }

    public required bool SupportsExactSpatialRelationshipPushdown { get; init; }

    public required bool SupportsTemporalFilterPushdown { get; init; }

    public required bool SupportsOrderByPushdown { get; init; }

    public required bool SupportsPagingPushdown { get; init; }
}
