// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Tests for MCP principal key derivation and session-binding identity selection.
/// </summary>
public sealed class McpAuthorizationHelperTests
{
    [UnitTest]
    public void ResolvePrincipalKey_IncludesAuthenticationScheme_WithNameIdentifier()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "operator-123"),
            new Claim(ClaimTypes.Name, "Operator One")
        ], "JwtBearer"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("JwtBearer:sub:operator-123");
    }

    [UnitTest]
    public void ResolvePrincipalKey_IncludesAuthenticationScheme_WithNameWhenNoSubject()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin")
        ], "ApiKey"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("ApiKey:name:admin");
        McpAuthorizationHelper.ResolveDistinctPrincipalKey(principal).Should().BeNull(
            "an API-key name is not guaranteed to identify one key");
    }

    [UnitTest]
    public void ResolveDistinctPrincipalKey_ApiKeyIdDoesNotMakeSharedNameASafeLegacyAlias()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "shared-studio-key"),
            new Claim("api_key_id", "11111111-2222-3333-4444-555555555555"),
        ], "ApiKey"));

        McpAuthorizationHelper.ResolveDistinctPrincipalKey(principal).Should().BeNull();
    }

    [UnitTest]
    public void ResolvePrincipalKey_UsesAnonymousForUnauthenticated()
    {
        var principal = new ClaimsPrincipal();

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be(McpSessionManager.AnonymousPrincipalKey);
    }

    [UnitTest]
    public void ResolvePrincipalKey_DifferentSchemesYieldDifferentKeys()
    {
        var bearer = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "identity-1")
        ], "JwtBearer"));
        var apiKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "identity-1"),
            new Claim(ClaimTypes.Name, "identity-1")
        ], "ApiKey"));

        McpAuthorizationHelper.ResolvePrincipalKey(bearer).Should().Be("JwtBearer:sub:identity-1");
        McpAuthorizationHelper.ResolvePrincipalKey(apiKey).Should().Be("ApiKey:sub:identity-1");
    }

    [Fact]
    public void ResolveDistinctPrincipalKey_WithOnlyRawSubject_FailsClosed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "identity-1"),
        ], "JwtBearer"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("JwtBearer:authenticated");
        McpAuthorizationHelper.ResolveDistinctPrincipalKey(principal).Should().BeNull();
    }
}
