// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class LayerRelationshipInfo
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required int RelatedTableId { get; init; }

    public required string Role { get; init; }

    public string Cardinality { get; init; } = "esriRelCardinalityOneToMany";

    public bool Composite { get; init; }

    public required string KeyField { get; init; }

    public required string OriginKeyField { get; init; }

    public required string DestinationKeyField { get; init; }

    public string? Description { get; init; }
}
