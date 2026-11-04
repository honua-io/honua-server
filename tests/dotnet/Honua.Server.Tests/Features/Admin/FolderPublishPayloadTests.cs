// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class FolderPublishPayloadTests
{
    [UnitTest]
    public void DurablePublishPayload_FolderSelection_PreservesOmittedRootAndFolder()
    {
        foreach (var selection in new MetadataV2FolderSelection[] { default, new(null), new("folder") })
        {
            var payload = new LayerPublishJobPayload
            {
                ConnectionId = Guid.NewGuid(), ConnectionKey = "test", RowCount = 1,
                Request = new PublishLayerRequest
                {
                    Schema = "public", Table = "parcels", LayerName = "Parcels",
                    ServiceName = "internal-parcels", PublicName = "Parcels", FolderId = selection
                }
            };
            var parameters = new Dictionary<string, string> { [LayerPublishJobPayload.ParameterName] = payload.ToJson() };
            LayerPublishJobPayload.TryParse(parameters, out var restored, out _).Should().BeTrue();
            restored!.Request.FolderId.Should().Be(selection);
            var domain = LayerPublishRequestMapper.CreateDomain(restored.Request, payload.ConnectionId, null);
            domain.FolderId.Should().Be(selection);
            LayerPublishRequestMapper.ToHttp(domain).PublicName.Should().Be("Parcels");
        }
    }
}
