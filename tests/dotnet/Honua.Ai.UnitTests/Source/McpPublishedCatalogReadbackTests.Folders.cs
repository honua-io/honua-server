// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpPublishedCatalogReadbackTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAndIndex_FolderMetadata_PreserveLogicalIdentity(bool organized)
    {
        var graph = Catalog();
        if (organized)
        {
            graph = graph with
            {
                Folders = [new() { Metadata = new() { Id = "folder", Name = "Public" } }],
                Services = graph.Services.Select(service => service with
                {
                    FolderId = "folder", PublicName = "Parcels", Metadata = service.Metadata with { Title = "Field parcels" }
                }).ToArray()
            };
        }
        var detail = await ReadAsync(graph);
        var index = new PromotionSurfaceIndexResource(_promotions, _deployments, _jobs,
            NullLogger<PromotionSurfaceIndexResource>.Instance);
        var result = await index.ReadAsync(Context(graph), "honua://published-services", CancellationToken.None);
        var summary = McpTestFactory.ParseJson(result.Contents[0].Text).GetProperty("items")[0];
        foreach (var projected in new[] { detail, summary })
        {
            projected.GetProperty("serviceId").GetString().Should().Be(ServiceName);
            projected.GetProperty("resourceUri").GetString().Should().Be(ServiceUri);
            projected.GetProperty("publicName").GetString().Should().Be(organized ? "Parcels" : ServiceName);
            if (organized)
            {
                projected.GetProperty("folderId").GetString().Should().Be("folder");
                projected.GetProperty("folderName").GetString().Should().Be("Public");
                projected.GetProperty("title").GetString().Should().Be("Field parcels");
            }
        }
    }
}
