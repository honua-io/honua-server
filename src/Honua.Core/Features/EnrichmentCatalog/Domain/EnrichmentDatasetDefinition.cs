// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Domain;

public sealed record EnrichmentDatasetDefinition(
    string Id,
    string? Title,
    string Category,
    int LayerId,
    string DefaultPredicate,
    double? DistanceMeters,
    IReadOnlyList<string> Attributes,
    string? Attribution,
    HonuaEdition MinimumEdition,
    string Source);
