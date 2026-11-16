// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public sealed record NetworkDataset(
    string Id,
    string Name,
    string EdgeTable,
    string VertexTable,
    int Srid = 4326)
{
    public IReadOnlyList<RoutingTravelProfile> TravelProfiles { get; init; } = [RoutingTravelProfile.Driving];

    public const string DefaultId = "default";

    public static NetworkDataset Default { get; } = new(
        DefaultId,
        "Default network dataset",
        "public.ways",
        "public.ways_vertices_pgr",
        4326);
}
