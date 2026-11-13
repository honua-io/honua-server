// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpBearerAuthenticationTests
{
    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task Post_TenantlessOperatorBearerToolCall_UsesDefaultTenantAndSucceeds()
    {
        using var request = BuildRpc(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""",
            sessionId: null,
            bearer: CreateOperatorToken(tenantId: null));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task Post_TenantlessOperatorBearerToolCall_WithoutDefaultTenant_IsDeniedBeforeToolExecution()
    {
        await using var fixture = CreateOperatorOnlyFixture(oidcEnabled: false)
            .ConfigureWebHost(builder => builder.UseSetting("MultiTenancy:DefaultTenantId", ""));
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        using var request = BuildRpc(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""",
            sessionId: null,
            bearer: CreateOperatorToken(tenantId: null));

        var response = await client.SendAsync(request);

        await AssertTenantDenialRpcErrorAsync(response);
    }

    // Multi-tenant mode runs the tenant middleware's resolution path (single-tenant mode returns
    // early), so these pin the operator bearer's default-tenant branch and its denial without one.
    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task Post_TenantlessOperatorBearerToolCall_MultiTenant_UsesDefaultTenantAndSucceeds()
    {
        await using var fixture = CreateOperatorOnlyFixture(oidcEnabled: false)
            .ConfigureWebHost(builder => builder.UseSetting("MultiTenancy:Enabled", "true"));
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        using var request = BuildRpc(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""",
            sessionId: null,
            bearer: CreateOperatorToken(tenantId: null));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task Post_TenantlessOperatorBearerToolCall_MultiTenantWithoutDefaultTenant_IsDenied()
    {
        await using var fixture = CreateOperatorOnlyFixture(oidcEnabled: false)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("MultiTenancy:Enabled", "true");
                builder.UseSetting("MultiTenancy:DefaultTenantId", "");
            });
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        using var request = BuildRpc(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""",
            sessionId: null,
            bearer: CreateOperatorToken(tenantId: null));

        var response = await client.SendAsync(request);

        await AssertTenantDenialRpcErrorAsync(response);
    }
}
