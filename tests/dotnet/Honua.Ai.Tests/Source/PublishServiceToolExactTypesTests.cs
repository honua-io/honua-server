// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class PublishServiceToolExactTypesTests
{
    [UnitTest]
    public void BuildRequest_ExactTypes_PassesTheOperationFlag()
    {
        var request = BuildRequest(new McpPublishServiceArgument
        {
            ConnectionId = "primary",
            Schema = "public",
            Table = "ledger",
            LayerName = "Ledger",
            ExactTypes = true
        });

        request.Parameters.Should().Contain("exactTypes", "true");
    }

    [UnitTest]
    public void BuildRequest_OmittedExactTypes_SendsNoFlag()
    {
        var request = BuildRequest(new McpPublishServiceArgument
        {
            ConnectionId = "primary",
            Schema = "public",
            Table = "ledger",
            LayerName = "Ledger"
        });

        request.Parameters.Should().NotContainKey("exactTypes");
    }

    private static OperationRequest BuildRequest(McpPublishServiceArgument argument)
        => (OperationRequest)typeof(PublishServiceTool)
            .GetMethod("BuildRequest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [argument])!;
}
