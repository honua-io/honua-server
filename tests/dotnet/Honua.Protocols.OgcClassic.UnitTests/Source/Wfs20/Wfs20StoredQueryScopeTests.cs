// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20StoredQueryScopeTests
{
    private const string WfsNs = "http://www.opengis.net/wfs/2.0";

    private static readonly InMemoryWfsStoredQueryStore StoredQueries = new();

    // ─── BH3-009 regression ──────────────────────────────────────────────────────

    [UnitTest]
    public async Task HandleDropStoredQuery_QueryFromOtherScope_ReturnsInvalidParameterValue()
    {
        var storedQueryId = $"urn:bh3-009:cross-drop:{Guid.NewGuid():N}";

        // Service A creates the query.
        var contextA = CreateContextWithTenant("tenant-a");
        SetParsedCreateDocument(contextA, storedQueryId);
        var createResult = await Wfs20Handler.HandleCreateStoredQueryAsync(contextA);
        createResult.Should().BeAssignableTo<IResult>("create must return an IResult");
        // The create should have succeeded (status 200 / CreateStoredQueryResponse).
        await AssertXmlResultStatusAsync(contextA, createResult, expectedContains: "CreateStoredQueryResponse");

        // Service B attempts to drop Service A's query — must be rejected.
        var contextB = CreateContextWithTenant("tenant-b");
        var dropResult = await Wfs20Handler.HandleDropStoredQueryAsync(contextB, storedQueryId);

        await AssertXmlResultStatusAsync(contextB, dropResult, expectedContains: "InvalidParameterValue",
            "a cross-scope drop must return InvalidParameterValue, not succeed");

        // Service A can still drop its own query.
        var ownDropResult = await Wfs20Handler.HandleDropStoredQueryAsync(contextA, storedQueryId);
        await AssertXmlResultStatusAsync(contextA, ownDropResult, expectedContains: "DropStoredQueryResponse",
            "owner tenant must be able to drop its own stored query");
    }

    [UnitTest]
    public async Task HandleCreateStoredQuery_TwoScopesReuseQueryId_BothSucceed()
    {
        // The same logical query ID may be registered independently in two different scopes.
        var sharedId = $"urn:bh3-009:shared-id:{Guid.NewGuid():N}";

        var contextA = CreateContextWithTenant("scope-x");
        SetParsedCreateDocument(contextA, sharedId);
        var resultA = await Wfs20Handler.HandleCreateStoredQueryAsync(contextA);
        await AssertXmlResultStatusAsync(contextA, resultA, expectedContains: "CreateStoredQueryResponse",
            "scope-x should be able to register the shared ID in its own namespace");

        var contextB = CreateContextWithTenant("scope-y");
        SetParsedCreateDocument(contextB, sharedId);
        var resultB = await Wfs20Handler.HandleCreateStoredQueryAsync(contextB);
        await AssertXmlResultStatusAsync(contextB, resultB, expectedContains: "CreateStoredQueryResponse",
            "scope-y must not see scope-x's registration, so the same ID must be accepted independently");

        // Cleanup: both scopes drop their own copy.
        await Wfs20Handler.HandleDropStoredQueryAsync(contextA, sharedId);
        await Wfs20Handler.HandleDropStoredQueryAsync(contextB, sharedId);
    }

    [UnitTest]
    public async Task HandleCreateStoredQuery_ViewerRole_ReturnsAccessDenied()
    {
        var context = CreateContextWithTenant("tenant-a");
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "viewer")],
                "test"));
        SetParsedCreateDocument(context, $"urn:srv-ogc-012:viewer:{Guid.NewGuid():N}");

        var result = await Wfs20Handler.HandleCreateStoredQueryAsync(context);

        var (status, body) = await ExecuteAsync(context, result);
        status.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("AccessDenied");
    }

    [UnitTest]
    public async Task HandleDropStoredQuery_ViewerRole_ReturnsAccessDenied()
    {
        var context = CreateContextWithTenant("tenant-a");
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "viewer")],
                "test"));

        var result = await Wfs20Handler.HandleDropStoredQueryAsync(context, "urn:srv-ogc-012:viewer");

        var (status, body) = await ExecuteAsync(context, result);
        status.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("AccessDenied");
    }

    [UnitTest]
    public async Task HandleCreateStoredQuery_GlobalDataEditor_Succeeds()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWfsStoredQueryStore>(StoredQueries);
        services.AddSingleton<ITenantContext>(new FixedTenantContext("tenant-editor"));
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<Honua.Infrastructure.Authentication.RbacOptions>>(
            Microsoft.Extensions.Options.Options.Create(new Honua.Infrastructure.Authentication.RbacOptions
            {
                DataEditorRoles = ["publisher"]
            }));
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "publisher")],
                    "test"))
        };
        context.Request.Path = "/wfs";
        SetParsedCreateDocument(context, $"urn:srv-ogc-012:editor:{Guid.NewGuid():N}");

        var result = await Wfs20Handler.HandleCreateStoredQueryAsync(context);

        await AssertXmlResultStatusAsync(context, result, expectedContains: "CreateStoredQueryResponse");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static DefaultHttpContext CreateContextWithTenant(string tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWfsStoredQueryStore>(StoredQueries);
        services.AddSingleton<ITenantContext>(new FixedTenantContext(tenantId));
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "admin")],
                    "test"))
        };
        // Set the WFS path so the error formatter produces WFS XML responses
        // (the formatter dispatches on path prefix via ProtocolRequestClassifier.IsWfs).
        context.Request.Path = "/wfs";
        return context;
    }

    private static void SetParsedCreateDocument(DefaultHttpContext context, string storedQueryId)
    {
        var xml = $"""
            <wfs:CreateStoredQuery service="WFS" version="2.0.0"
                xmlns:wfs="{WfsNs}" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <wfs:StoredQueryDefinition id="{storedQueryId}">
                <wfs:Title>BH3-009 scope test</wfs:Title>
                <wfs:QueryExpressionText returnFeatureTypes=""
                    language="urn:ogc:def:queryLanguage:OGC-WFS::WFSQueryExpression" isPrivate="false">
                  <wfs:Query typeNames="honua:test" />
                </wfs:QueryExpressionText>
              </wfs:StoredQueryDefinition>
            </wfs:CreateStoredQuery>
            """;
        context.Items[Wfs20DispatcherEndpoint.ParsedXmlDocumentItemKey] = XDocument.Parse(xml);
    }

    private static async Task AssertXmlResultStatusAsync(
        DefaultHttpContext originalContext,
        IResult result,
        string expectedContains,
        string? because = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = originalContext.RequestServices
        };
        httpContext.Response.Body = new System.IO.MemoryStream();

        await result.ExecuteAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var reader = new System.IO.StreamReader(httpContext.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        body.Should().Contain(expectedContains,
            because ?? $"response body must contain '{expectedContains}'");
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(DefaultHttpContext originalContext, IResult result)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = originalContext.RequestServices
        };
        httpContext.Request.Path = "/wfs";
        httpContext.Response.Body = new System.IO.MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new System.IO.StreamReader(httpContext.Response.Body, Encoding.UTF8);
        return (httpContext.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class FixedTenantContext(string tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source => TenantContextSource.Claim;

        public bool RequireTenantId(out string outTenantId, out string? reason)
        {
            outTenantId = TenantId!;
            reason = null;
            return true;
        }
    }
}
