// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Tests.Features.Console;

public sealed partial class PublishedContentProjectionTests
{
    private static MetadataV2Graph Graph() => new()
    {
        Revision = 1,
        Folders = [new() { Metadata = new() { Id = "folder", Name = "Survey" } }],
        Services = [new()
        {
            Metadata = new() { Id = "service", Name = "internal", Title = "Parcels" },
            PublicName = "Parcels", FolderId = "folder", Protocols = [ServiceProtocols.FeatureServer],
            Status = new() { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new() { AllowAnonymous = true }
        }],
        Resources = [new()
        {
            Metadata = new() { Id = "resource", Name = "parcels", Title = "Parcels" },
            Status = new() { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new() { AllowAnonymous = true }
        }],
        Publications = [new()
        {
            Metadata = new() { Id = "publication", Name = "parcels" },
            IsPrimary = true,
            ServiceId = "service", ResourceId = "resource", LayerIndex = 0,
            Status = new() { Lifecycle = MetadataV2LifecycleStatus.Active }
        }]
    };
}
