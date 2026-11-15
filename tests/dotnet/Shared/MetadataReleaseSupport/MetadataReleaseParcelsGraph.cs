// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Tests.MetadataRelease;

internal static class MetadataReleaseParcelsGraph
{
    public static MetadataV2StorageBinding StorageBinding(string environment)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels", Name = "storage.parcels" },
            ResourceId = "res.parcels",
            ConnectionId = "conn.parcels",
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{environment}_schema.parcels",
        };

    public static MetadataV2Service FeaturesService(string environment)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc.features", Name = "features" },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures,
            Route = $"/{environment}/ogc/features",
            PublicationIds = ["pub.parcels"],
        };

    public static MetadataV2Layer Publication()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels", Name = "parcels" },
            ResourceId = "res.parcels",
            ServiceId = "svc.features",
            StorageBindingId = "storage.parcels",
            PublicationType = MetadataV2LayerType.OgcCollection,
            Path = "parcels",
            ServiceLocalId = "parcels",
        };
}
