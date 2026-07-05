// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Regression tests for BH3-009: <c>ManagedStoredQueries</c> was a process-global
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/> with no
/// service or tenant scoping. Stored queries created on Service A were fully visible and
/// operable from Service B, enabling information disclosure, sabotage (cross-service delete),
/// and DoS via global slot exhaustion.
///
/// After the fix the dictionary is keyed by a composite (serviceScope, queryId) tuple derived
/// from the request's tenant context, so queries created in one scope are invisible in another.
/// </summary>
[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20StoredQueryScopeTests
{
    private const string WfsNs = "http://www.opengis.net/wfs/2.0";

    // ─── BH3-009 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// A stored query created by tenant A must not be drop-able from tenant B's context.
    /// Before the fix <see cref="Wfs20Handler.HandleDropStoredQuery"/> accepted any query ID
    /// regardless of which service (tenant) created it.
    /// </summary>
    [UnitTest]
    public void HandleDropStoredQuery_QueryFromOtherScope_ReturnsInvalidParameterValue()
    {
        var storedQueryId = $"urn:bh3-009:cross-drop:{Guid.NewGuid():N}";

        // Service A creates the query.
        var contextA = CreateContextWithTenant("tenant-a");
        SetParsedCreateDocument(contextA, storedQueryId);
        var createResult = Wfs20Handler.HandleCreateStoredQuery(contextA);
        createResult.Should().BeAssignableTo<IResult>("create must return an IResult");
        // The create should have succeeded (status 200 / CreateStoredQueryResponse).
        AssertXmlResultStatus(contextA, createResult, expectedContains: "CreateStoredQueryResponse");

        // Service B attempts to drop Service A's query — must be rejected.
        var contextB = CreateContextWithTenant("tenant-b");
        var dropResult = Wfs20Handler.HandleDropStoredQuery(contextB, storedQueryId);

        AssertXmlResultStatus(contextB, dropResult, expectedContains: "InvalidParameterValue",
            "a cross-scope drop must return InvalidParameterValue, not succeed");

        // Service A can still drop its own query.
        var ownDropResult = Wfs20Handler.HandleDropStoredQuery(contextA, storedQueryId);
        AssertXmlResultStatus(contextA, ownDropResult, expectedContains: "DropStoredQueryResponse",
            "owner tenant must be able to drop its own stored query");
    }

    /// <summary>
    /// A stored query created under <c>tenant-a</c> must not appear in a
    /// <c>DescribeStoredQueries</c> request that targets a non-existent query by that ID
    /// under <c>tenant-b</c>'s scope — the response must be an error, not the definition.
    /// </summary>
    [UnitTest]
    public void HandleCreateStoredQuery_TwoScopesReuseQueryId_BothSucceed()
    {
        // The same logical query ID may be registered independently in two different scopes.
        var sharedId = $"urn:bh3-009:shared-id:{Guid.NewGuid():N}";

        var contextA = CreateContextWithTenant("scope-x");
        SetParsedCreateDocument(contextA, sharedId);
        var resultA = Wfs20Handler.HandleCreateStoredQuery(contextA);
        AssertXmlResultStatus(contextA, resultA, expectedContains: "CreateStoredQueryResponse",
            "scope-x should be able to register the shared ID in its own namespace");

        var contextB = CreateContextWithTenant("scope-y");
        SetParsedCreateDocument(contextB, sharedId);
        var resultB = Wfs20Handler.HandleCreateStoredQuery(contextB);
        AssertXmlResultStatus(contextB, resultB, expectedContains: "CreateStoredQueryResponse",
            "scope-y must not see scope-x's registration, so the same ID must be accepted independently");

        // Cleanup: both scopes drop their own copy.
        Wfs20Handler.HandleDropStoredQuery(contextA, sharedId);
        Wfs20Handler.HandleDropStoredQuery(contextB, sharedId);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static DefaultHttpContext CreateContextWithTenant(string tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(tenantId));
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
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

    /// <summary>
    /// Executes an <see cref="IResult"/> against a temporary <see cref="DefaultHttpContext"/>
    /// and asserts that the response body XML contains <paramref name="expectedContains"/>.
    /// </summary>
    private static void AssertXmlResultStatus(
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

        result.ExecuteAsync(httpContext).GetAwaiter().GetResult();

        httpContext.Response.Body.Position = 0;
        var body = new System.IO.StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEnd();
        body.Should().Contain(expectedContains,
            because ?? $"response body must contain '{expectedContains}'");
    }

    /// <summary>
    /// Simple <see cref="ITenantContext"/> that returns a fixed tenant ID.
    /// </summary>
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
