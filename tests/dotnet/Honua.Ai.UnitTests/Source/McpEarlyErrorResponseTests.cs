// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit coverage for errors emitted before the MCP dispatcher runs.
/// </summary>
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpEarlyErrorResponseTests
{
    [UnitTest]
    public void ResponseIdForEarlyError_RevokedKeyRequest_PreservesPendingRequestId()
    {
        using var document = JsonDocument.Parse(
            """{"jsonrpc":"2.0","id":"revoked-key-call","method":"tools/call","params":{}}""");

        var responseId = McpEndpointExtensions.ResponseIdForEarlyError(document.RootElement);

        responseId.GetString().Should().Be("revoked-key-call");
    }
}
