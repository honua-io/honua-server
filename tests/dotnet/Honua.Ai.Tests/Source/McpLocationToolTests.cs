// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geoprocessing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the MCP location tools added in #1597. These tests run
/// through the JSON-RPC <c>tools/call</c> dispatcher while substituting the
/// canonical geocoding/routing services, so they validate the MCP adapter
/// behavior without external geocoder or pgRouting dependencies.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpLocationToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_address")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddress_DelegatesAndReturnsCandidates()
    {
        var coordinator = Substitute.For<IGeocodeCoordinatorService>();
        coordinator.ForwardGeocodeAsync(
                Arg.Is<ForwardGeocodeRequest>(request =>
                    request.Query == "1600 Amphitheatre Parkway" &&
                    request.MaxResults == 1 &&
                    request.SpatialReferenceWkid == 4326),
                "mock",
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>(
            [
                new GeocodeCandidate(
                    "1600 Amphitheatre Pkwy, Mountain View, CA",
                    -122.084,
                    37.422,
                    99.1,
                    new Dictionary<string, string?>())
                {
                    SpatialReferenceWkid = 4326,
                    MatchLevel = "PointAddress",
                    AddressType = "StreetAddress"
                }
            ], "mock"));

        var services = BuildServices(
            ActiveLicense("geocoding.forward"),
            geocodeCoordinator: coordinator);
        var surface = new McpDataAccessSurface(
            [new GeocodeTool(_jobService, NullLogger<GeocodeTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("geo-1", GeocodeTool.ToolName, """
                {"address":"1600 Amphitheatre Parkway","maxResults":1,"provider":"mock"}
                """),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("provider").GetString().Should().Be("mock");
        var candidate = structured.GetProperty("candidates")[0];
        candidate.GetProperty("address").GetString().Should().Contain("Mountain View");
        candidate.GetProperty("x").GetDouble().Should().BeApproximately(-122.084, 0.000001);
        candidate.GetProperty("y").GetDouble().Should().BeApproximately(37.422, 0.000001);
        candidate.GetProperty("score").GetDouble().Should().Be(99.1);
        StructuredContentShouldMatchOutputSchema(result, McpToolOutputSchemas.GeocodeOutputSchema);

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.Process,
            OperatorOperation.Execute,
            Arg.Any<CancellationToken>());
        await coordinator.Received(1).ForwardGeocodeAsync(
            Arg.Any<ForwardGeocodeRequest>(),
            "mock",
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_solve_route")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_SolveRoute_DelegatesAndReturnsRouteResult()
    {
        var routingProvider = Substitute.For<IRoutingProvider>();
        routingProvider.Name.Returns("test-routing");
        routingProvider.Capabilities.Returns(new RoutingProviderCapabilities(SupportsRoute: true));
        routingProvider.SolveRouteAsync(
                Arg.Is<RouteSolveRequest>(request =>
                    request.Stops.Count == 2 &&
                    request.TravelProfile == "walking" &&
                    request.InSrid == 4326 &&
                    request.OutSrid == 3857),
                Arg.Any<CancellationToken>())
            .Returns(new RouteSolveResult(
                """{"type":"LineString","coordinates":[[-122.1,37.4],[-122.2,37.5]]}""",
                1234.5,
                12.3,
                [new RouteDirectionStep("Go straight", 1234.5, 12.3, "straight")]));

        var services = BuildServices(
            ActiveLicense(RouteTool.EntitlementKey),
            routingProvider: routingProvider);
        var surface = new McpDataAccessSurface(
            [new RouteTool(_jobService, NullLogger<RouteTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("route-1", RouteTool.ToolName, """
                {
                  "stops":[
                    {"lon":-122.1,"lat":37.4},
                    {"lon":-122.2,"lat":37.5}
                  ],
                  "travelProfile":"walking",
                  "inSrid":4326,
                  "outSrid":3857
                }
                """),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("provider").GetString().Should().Be("test-routing");
        structured.GetProperty("solved").GetBoolean().Should().BeTrue();
        structured.GetProperty("totalLengthMeters").GetDouble().Should().Be(1234.5);
        structured.GetProperty("totalTimeMinutes").GetDouble().Should().Be(12.3);
        structured.GetProperty("directions")[0].GetProperty("maneuverType").GetString().Should().Be("straight");

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.Process,
            OperatorOperation.Execute,
            Arg.Any<CancellationToken>());
        await routingProvider.Received(1).SolveRouteAsync(
            Arg.Any<RouteSolveRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RouteTool.ToolName, RouteTool.EntitlementKey, """{"stops":[{"lon":0,"lat":0},{"lon":1,"lat":1}]}""")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_WithoutEntitlement_ReturnsFailedPreconditionInsideResult(
        string toolName,
        string entitlementKey,
        string argumentsJson)
    {
        var services = BuildServices(InactiveLicense(entitlementKey));
        var surface = new McpDataAccessSurface(
            [
                new GeocodeTool(_jobService, NullLogger<GeocodeTool>.Instance),
                new RouteTool(_jobService, NullLogger<RouteTool>.Instance)
            ],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("gate-1", toolName, argumentsJson),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("failed_precondition");
        structured.GetProperty("message").GetString().Should().Contain(entitlementKey);
    }

    // #2981: forward geocoding moved to Community — the demo/adoption showcase path — so the
    // tool must succeed even when no license/entitlement service reports it active. This is the
    // MCP-side mirror of the HTTP GeocodingEntitlementGateTests forward/reverse assertions;
    // together they prove the HTTP and MCP surfaces agree on the re-tiered keys.
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_address")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddress_WithoutAnyActiveEntitlement_StillSucceeds()
    {
        var coordinator = Substitute.For<IGeocodeCoordinatorService>();
        coordinator.ForwardGeocodeAsync(
                Arg.Any<ForwardGeocodeRequest>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>(
            [
                new GeocodeCandidate("1 Main St, Anytown", 0, 0, 90.0, new Dictionary<string, string?>())
            ], "mock"));

        var services = BuildServices(
            InactiveLicense("geocoding.forward"),
            geocodeCoordinator: coordinator);
        var surface = new McpDataAccessSurface(
            [new GeocodeTool(_jobService, NullLogger<GeocodeTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("geo-community-1", GeocodeTool.ToolName, """
                {"address":"1 Main St"}
                """),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_address")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddress_InvalidArguments_ReturnsSchemaValidToolError()
    {
        // #2578 error-shape contract: an invalid geocode call must surface a
        // structured tool error (isError=true) whose envelope still validates
        // against the advertised outputSchema (the oneOf error branch), not a
        // bare protocol error.
        var services = BuildServices(
            ActiveLicense("geocoding.forward"),
            geocodeCoordinator: Substitute.For<IGeocodeCoordinatorService>());
        var surface = new McpDataAccessSurface(
            [new GeocodeTool(_jobService, NullLogger<GeocodeTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("geo-invalid-1", GeocodeTool.ToolName, """
                {"address":"   "}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("error");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("error").GetProperty("kind").GetString().Should().Be("ValidationFailed");
        StructuredContentShouldMatchOutputSchema(result, McpToolOutputSchemas.GeocodeOutputSchema);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_address")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddress_ProviderFailure_ReturnsSchemaValidToolError()
    {
        // #2578 round-trip contract: when no provider produces a result the tool
        // surfaces a structured, retryable tool error whose envelope validates
        // against the advertised outputSchema.
        var coordinator = Substitute.For<IGeocodeCoordinatorService>();
        coordinator.ForwardGeocodeAsync(
                Arg.Any<ForwardGeocodeRequest>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Failure<IReadOnlyList<GeocodeCandidate>>(
                "no provider produced a result.",
                "mock"));

        var services = BuildServices(
            ActiveLicense("geocoding.forward"),
            geocodeCoordinator: coordinator);
        var surface = new McpDataAccessSurface(
            [new GeocodeTool(_jobService, NullLogger<GeocodeTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("geo-fail-1", GeocodeTool.ToolName, """
                {"address":"1600 Amphitheatre Parkway"}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("error");
        structured.GetProperty("error").GetProperty("kind").GetString().Should().Be("ExecutionFailed");
        StructuredContentShouldMatchOutputSchema(result, McpToolOutputSchemas.GeocodeOutputSchema);
    }

    private static ServiceProvider BuildServices(
        ILicenseEntitlementService license,
        IGeocodeCoordinatorService? geocodeCoordinator = null,
        IRoutingProvider? routingProvider = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(license);
        if (geocodeCoordinator is not null)
        {
            services.AddSingleton(geocodeCoordinator);
        }

        if (routingProvider is not null)
        {
            services.AddSingleton(routingProvider);
        }

        return services.BuildServiceProvider();
    }

    private static ILicenseEntitlementService ActiveLicense(string entitlementKey)
    {
        var license = Substitute.For<ILicenseEntitlementService>();
        license.CheckEntitlement(entitlementKey)
            .Returns(new LicenseEntitlementDecision(
                entitlementKey,
                true,
                HonuaEdition.Pro,
                LicenseValidationState.Valid,
                HonuaEdition.Pro,
                string.Empty));
        return license;
    }

    private static ILicenseEntitlementService InactiveLicense(string entitlementKey)
    {
        var license = Substitute.For<ILicenseEntitlementService>();
        license.CheckEntitlement(entitlementKey)
            .Returns(new LicenseEntitlementDecision(
                entitlementKey,
                false,
                HonuaEdition.Community,
                LicenseValidationState.NoLicenseConfigured,
                HonuaEdition.Pro,
                $"{entitlementKey} requires an active Pro entitlement."));
        return license;
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        return context;
    }

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{toolName}}","arguments":{{argumentsJson}}}
            """)
    };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void StructuredContentShouldMatchOutputSchema(JsonElement result, JsonElement outputSchema)
    {
        var schema = JSchema.Parse(outputSchema.GetRawText());
        var payload = JToken.Parse(result.GetProperty("structuredContent").GetRawText());

        payload.IsValid(schema, out IList<string> errors).Should().BeTrue(
            "structuredContent must match the advertised outputSchema. Errors: {0}",
            string.Join("; ", errors));
    }
}
