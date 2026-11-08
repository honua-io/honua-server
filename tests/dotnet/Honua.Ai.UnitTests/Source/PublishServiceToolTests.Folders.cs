// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishServiceToolTests
{
    [UnitTheory]
    [InlineData("", false, null)]
    [InlineData(",\"folderId\":null,\"publicName\":\"Parcels\",\"namespace\":\"workspace\"", true, null)]
    [InlineData(",\"folderId\":\"folder\",\"publicName\":\"Parcels\",\"namespace\":\"workspace\"", true, "folder")]
    public async Task PublishService_ForwardsFolderPresenceAndPublicName(string selection, bool specified, string? folderId)
    {
        OperationRequest? captured = null;
        var invoker = new FakeInvoker((request, _) =>
        {
            captured = request;
            return new OperationHandle
            {
                OperationInstanceId = "publish-folder-test", OperationId = request.OperationId,
                Status = OperationHandleStatus.Completed, CorrelationId = "publish-folder-test",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
        });
        var arguments = McpTestFactory.ParseJson($$"""
            {"connectionId":"11111111-1111-1111-1111-111111111111","schema":"public","table":"points","layerName":"Points"{{selection}}}
            """);
        var result = await CreateTool().InvokeAsync(ContextWithInvoker(invoker), arguments, CancellationToken.None);
        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.Parameters.ContainsKey("folderId").Should().Be(specified);
        if (specified)
        {
            captured.Parameters["folderId"].Should().Be(folderId);
            captured.Parameters["publicName"].Should().Be("Parcels");
            captured.Parameters["namespace"].Should().Be("workspace");
        }
        else captured.Parameters.Should().NotContainKey("publicName").And.NotContainKey("namespace");
    }
}
