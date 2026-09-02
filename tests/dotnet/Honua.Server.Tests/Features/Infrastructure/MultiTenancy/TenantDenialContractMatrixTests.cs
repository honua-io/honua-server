// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Protocols.OData;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.MultiTenancy;

/// <summary>
/// Full tenant-middleware denial matrix for the eight release protocol families (issue #3904).
/// Each cell proves the denial is native, correlated, and terminates before the route handler.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class TenantDenialContractMatrixTests
{
    private const string CorrelationId = "tenant-denial-correlation";

    public static TheoryData<string, string> ContractCells
    {
        get
        {
            var cells = new TheoryData<string, string>();
            foreach (var route in Routes)
            {
                foreach (var denial in Denials)
                {
                    cells.Add(route.Name, denial.Name);
                }
            }

            return cells;
        }
    }

    [UnitTest]
    public void ContractCells_ContainsExactEightByFourDenominator()
    {
        Routes.Should().HaveCount(8);
        Denials.Should().HaveCount(4);
        ContractCells.Count.Should().Be(32);
    }

    [Theory]
    [MemberData(nameof(ContractCells))]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    [Operation(Operations.Security)]
    [Endpoint("GET|POST tenant denial protocol matrix")]
    public async Task TenantDenial_RouteAndCause_UsesNativeEnvelopeWithoutHandlerSideEffects(
        string routeName,
        string denialName)
    {
        var route = Routes.Single(candidate => candidate.Name == routeName);
        var denial = Denials.Single(candidate => candidate.Name == denialName);
        var handlerCalls = new StrongBox<int>();
        await using var app = await CreateAppAsync(route, denial, handlerCalls);
        using var request = CreateRequest(route, denial);

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        handlerCalls.Value.Should().Be(0, "tenant denial must terminate before endpoint execution");
        response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle(CorrelationId);
        AssertNativeContract(route, denial, response, body);
    }

    private static void AssertNativeContract(
        RouteCase route,
        DenialCase denial,
        HttpResponseMessage response,
        string body)
    {
        switch (route.Name)
        {
            case "OGC API":
            case "Admin":
                {
                    response.StatusCode.Should().Be(denial.HttpStatus);
                    response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
                    using var document = JsonDocument.Parse(body);
                    var problem = document.RootElement;
                    problem.GetProperty("code").GetString().Should().Be(denial.Code);
                    problem.GetProperty("correlationId").GetString().Should().Be(CorrelationId);
                    break;
                }

            case "OData":
                {
                    response.StatusCode.Should().Be(denial.HttpStatus);
                    response.Headers.GetValues("OData-Version").Should().ContainSingle("4.01");
                    using var document = JsonDocument.Parse(body);
                    var error = document.RootElement.GetProperty("error");
                    error.GetProperty("code").GetString().Should().Be(denial.Code);
                    error.GetProperty("details").EnumerateArray().Should().Contain(detail =>
                        detail.GetProperty("code").GetString() == "CorrelationId"
                        && detail.GetProperty("message").GetString() == CorrelationId);
                    break;
                }

            case "GeoServices":
                {
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                    using var document = JsonDocument.Parse(body);
                    var error = document.RootElement.GetProperty("error");
                    error.GetProperty("code").GetInt32().Should().Be(
                        denial.HttpStatus == HttpStatusCode.Unauthorized ? 499 : 403);
                    var details = error.GetProperty("details").EnumerateArray()
                        .Select(value => value.GetString())
                        .ToArray();
                    details.Should().Contain($"Code: {denial.Code}");
                    details.Should().Contain($"CorrelationId: {CorrelationId}");
                    break;
                }

            case "WFS":
                {
                    response.StatusCode.Should().Be(denial.HttpStatus);
                    response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
                    body.Should().Contain($"exceptionCode=\"{denial.XmlCode}\"");
                    body.Should().Contain($"CorrelationId: {CorrelationId}");
                    break;
                }

            case "WMS alias":
                {
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                    response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
                    body.Should().Contain($"code=\"{denial.XmlCode}\"");
                    body.Should().Contain($"CorrelationId: {CorrelationId}");
                    break;
                }

            case "MCP JSON-RPC":
                {
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                    root.GetProperty("id").GetInt32().Should().Be(3904);
                    var error = root.GetProperty("error");
                    error.GetProperty("code").GetInt32().Should().Be(-32000);
                    error.GetProperty("data").GetProperty("code").GetString().Should().Be(denial.Code);
                    error.GetProperty("data").GetProperty("correlationId").GetString().Should().Be(CorrelationId);
                    break;
                }

            case "gRPC":
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be("application/grpc");
                response.Headers.GetValues("grpc-status").Should().ContainSingle(
                    denial.HttpStatus == HttpStatusCode.Unauthorized ? "16" : "7");
                response.Headers.GetValues("honua-error-code").Should().ContainSingle(denial.Code);
                response.Headers.GetValues("correlation-id").Should().ContainSingle(CorrelationId);
                body.Should().BeEmpty();
                break;

            default:
                throw new InvalidOperationException($"Unknown route family {route.Name}.");
        }

        body.Should().NotBe("{\"error\":\"forbidden\",\"message\":\"Forbidden\"}");
    }

    private static async Task<WebApplication> CreateAppAsync(
        RouteCase route,
        DenialCase denial,
        StrongBox<int> handlerCalls)
    {
        StandardErrorResponseFormatter.ODataErrorFormatterOverride = ODataErrorFormatter.Format;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHonuaTenantContext(new ConfigurationBuilder().Build());
        builder.Services.AddSingleton<ITenantCatalog>(new StubTenantCatalog(denial.TenantStatus));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.TraceIdentifier = CorrelationId;
            context.User = CreatePrincipal(denial);
            await next();
        });
        app.UseHonuaTenantContext();
        app.UseHonuaTenantStatusEnforcement();
        app.MapMethods(route.Path, [HttpMethods.Get, HttpMethods.Post], () =>
        {
            Interlocked.Increment(ref handlerCalls.Value);
            return Results.Text("handler-called");
        });

        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage CreateRequest(RouteCase route, DenialCase denial)
    {
        var request = new HttpRequestMessage(
            route.Name is "MCP JSON-RPC" or "gRPC" ? HttpMethod.Post : HttpMethod.Get,
            route.Path);

        if (denial.Name == "unauthorized override")
        {
            request.Headers.Add(TenantContextOptions.TenantHeaderName, "tenant-target");
        }

        if (route.Name == "MCP JSON-RPC")
        {
            request.Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":3904,\"method\":\"tools/call\",\"params\":{\"name\":\"honua_query_features\",\"arguments\":{}}}",
                Encoding.UTF8,
                "application/json");
        }
        else if (route.Name == "gRPC")
        {
            request.Version = HttpVersion.Version20;
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        }

        return request;
    }

    private static ClaimsPrincipal CreatePrincipal(DenialCase denial)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "matrix-user"),
        };

        if (denial.Name != "missing bearer tenant")
        {
            claims.Add(new Claim("tid", "tenant-home"));
        }

        var identity = new ClaimsIdentity(claims, "Bearer");
        CanonicalSecurityActor.StampFrameworkClaim(
            identity,
            CanonicalSecurityActor.AuthenticationSchemeClaim,
            "Bearer");
        return new ClaimsPrincipal(identity);
    }

    private static readonly RouteCase[] Routes =
    [
        new("OGC API", "/ogc/features/collections"),
        new("WFS", "/wfs"),
        new("WMS alias", "/ogc/services/example/wms"),
        new("OData", "/odata/Features"),
        new("GeoServices", "/rest/services/example/FeatureServer"),
        new("Admin", "/api/v1/admin/metadata/services"),
        new("MCP JSON-RPC", "/mcp"),
        new("gRPC", "/honua.v1.FeatureService/GetFeature"),
    ];

    private static readonly DenialCase[] Denials =
    [
        new("unauthorized override", "permission_denied", "AccessDenied", HttpStatusCode.Forbidden, null),
        new("missing bearer tenant", "authentication_required", "AuthenticationRequired", HttpStatusCode.Unauthorized, null),
        new("suspended tenant", "tenant_suspended", "TenantSuspended", HttpStatusCode.Forbidden, TenantStatus.Suspended),
        new("deleted tenant", "tenant_deleted", "TenantDeleted", HttpStatusCode.Forbidden, TenantStatus.Deleted),
    ];

    private sealed record RouteCase(string Name, string Path);

    private sealed record DenialCase(
        string Name,
        string Code,
        string XmlCode,
        HttpStatusCode HttpStatus,
        TenantStatus? TenantStatus);

    private sealed class StubTenantCatalog(TenantStatus? blockedStatus) : ITenantCatalog
    {
        private readonly ConcurrentDictionary<string, TenantRecord> _tenants = CreateTenants(blockedStatus);

        public Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            _tenants.TryGetValue(tenantId, out var record);
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TenantRecord>>([.. _tenants.Values]);

        public Task<bool> TryAddAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
            => Task.FromResult(_tenants.TryAdd(tenant.TenantId, tenant));

        public Task<TenantRecord?> UpdateAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
        {
            _tenants[tenant.TenantId] = tenant;
            return Task.FromResult<TenantRecord?>(tenant);
        }

        private static ConcurrentDictionary<string, TenantRecord> CreateTenants(TenantStatus? status)
        {
            var tenants = new ConcurrentDictionary<string, TenantRecord>(StringComparer.OrdinalIgnoreCase);
            if (status.HasValue)
            {
                tenants["tenant-home"] = new TenantRecord
                {
                    TenantId = "tenant-home",
                    DisplayName = "Tenant home",
                    Status = status.Value,
                };
            }

            return tenants;
        }
    }
}
