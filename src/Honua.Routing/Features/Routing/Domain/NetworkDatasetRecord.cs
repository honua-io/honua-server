// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public sealed record NetworkDatasetRecord(
    string Id,
    string Name,
    string EdgeTable,
    string VertexTable,
    int Srid,
    string Status,
    int TopologyVersion,
    bool NeedsRebuild,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy);
