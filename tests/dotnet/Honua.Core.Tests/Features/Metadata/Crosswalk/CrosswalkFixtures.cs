// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Tests.Features.Metadata.Crosswalk;

internal static class CrosswalkFixtures
{
    public static readonly DateTimeOffset Updated = new(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

    public static MetadataV2Graph Graph(
        MetadataV2ObjectMetadata? resource = null,
        MetadataV2ObjectMetadata? service = null,
        MetadataV2ObjectMetadata? folder = null,
        MetadataV2ObjectMetadata? environment = null)
    {
        return new()
        {
            Environment = "county",
            GeneratedAt = Updated,
            Metadata = environment ?? new MetadataV2ObjectMetadata { Id = "graph", Name = "graph" },
            Folders = [new MetadataV2Folder { Metadata = folder ?? new MetadataV2ObjectMetadata { Id = "folder.parks", Name = "parks" } }],
            Services =
            [
                new MetadataV2Service
                {
                    FolderId = "folder.parks",
                    Metadata = service ?? new MetadataV2ObjectMetadata { Id = "service.parks", Name = "parks-service" },
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = resource ?? new MetadataV2ObjectMetadata { Id = "dataset.trails", Name = "trails" },
                    Spatial = new MetadataV2ResourceSpatial
                    {
                        SpatialReference = MetadataV2SpatialReference.Wgs84,
                        Bbox = new MetadataV2Bbox { West = -123.5, South = 45.1, East = -122.25, North = 46 },
                        SupportedCrs = [new MetadataV2SpatialReference { Srid = 26910 }],
                    },
                    Temporal = new MetadataV2ResourceTemporal
                    {
                        Extent = new MetadataV2TimeRange { Start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                    },
                },
            ],
        };
    }

    public static MetadataV2ObjectMetadata FullDataset => new()
    {
        Id = "dataset.trails",
        Name = "trails",
        Title = "County trails",
        Description = "Maintained trail centerlines.",
        Keywords = ["trails", "recreation"],
        Themes = ["TRAN", "http://inspire.ec.europa.eu/theme/tn"],
        TopicCategories = ["transportation", "disaster"],
        Language = "en-US",
        License = "CC-BY-4.0",
        AccessConstraints = "Public, no restrictions.",
        Fees = "None",
        Attribution = "County GIS",
        Publisher = "County of Example",
        Contacts =
        [
            new MetadataV2ResponsibleParty { Role = "pointOfContact", Name = "Ana Ruiz", Organization = "County GIS", Email = "gis@example.org" },
            new MetadataV2ResponsibleParty { Role = "rightsHolder", Organization = "County of Example" },
        ],
        Lineage = "Digitized from 2024 orthoimagery.",
        UpdateFrequency = "monthly",
        SpatialResolution = new MetadataV2SpatialResolution { DistanceMeters = 0.5 },
        TemporalResolution = "P1D",
        Links = [new MetadataV2Link { Rel = "describedby", Href = "https://example.org/trails.html" }],
        UpdatedAt = Updated,
    };

    public static DescriptiveMetadata FullRecord()
    {
        var graph = Graph(resource: FullDataset);
        return DescriptiveMetadataResolver.ForDataset(graph, new(graph.Resources[0], graph.Services[0])) with
        {
            Distributions = [new DescriptiveDistribution("https://maps.example.org/ogc/collections/trails", "OGC API - Features", "Trails", "application/geo+json")],
        };
    }
}
