// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

internal static class FolderServiceTestCatalog
{
    public static MetadataV2Graph WithPublicParcels(MetadataV2Graph graph)
        => graph with
        {
            Folders = [new() { Metadata = new() { Id = "folder-routing", Name = "Public" } }],
            Services = graph.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                ? service with { FolderId = "folder-routing", PublicName = "Parcels" } : service).ToArray()
        };

    public static void ReplaceGraph(WebAppFixture fixture, MetadataV2Graph graph)
        => ((TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graph, schema: fixture.CurrentSchema);
}
