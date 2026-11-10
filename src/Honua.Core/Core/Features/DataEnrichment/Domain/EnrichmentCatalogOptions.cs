// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.DataEnrichment.Domain;

public sealed class EnrichmentCatalogOptions
{
    public const string SectionName = "DataEnrichment";

    public IList<EnrichmentDatasetOptions> Datasets { get; set; } = new List<EnrichmentDatasetOptions>();
}

public sealed class EnrichmentDatasetOptions
{
    [Required]
    public string Key { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? Category { get; set; }

    [Range(0, int.MaxValue)]
    public int LayerId { get; set; }

    public string Predicate { get; set; } = "intersects";

    public double? DistanceMeters { get; set; }

    public IList<string> Attributes { get; set; } = new List<string>();
}
