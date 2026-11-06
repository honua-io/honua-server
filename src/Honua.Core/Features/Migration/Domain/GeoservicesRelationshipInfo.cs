// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoservicesRelationshipInfo
{
    public int? Id { get; init; }

    public string? Name { get; init; }

    public int? RelatedTableId { get; init; }

    public string? KeyField { get; init; }

    public string? Role { get; init; }

    public string? Cardinality { get; init; }

    public bool? Composite { get; init; }
}
