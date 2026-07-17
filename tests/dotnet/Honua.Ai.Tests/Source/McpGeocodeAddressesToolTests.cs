// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Location;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Coverage for <c>honua_geocode_addresses</c>: the batch companion to the
/// single-address geocode tool. Runs through the JSON-RPC <c>tools/call</c>
/// dispatcher with the canonical coordinator substituted, so it validates the
/// per-item result contract (input order preserved, partial failures isolated)
/// without external geocoder dependencies.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpGeocodeAddressesToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_addresses")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddresses_PreservesOrderAndIsolatesPartialFailures()
    {
        var coordinator = Substitute.For<IGeocodeCoordinatorService>();
        coordinator.ForwardGeocodeAsync(
                Arg.Is<ForwardGeocodeRequest>(r => r.Query == "1100 Congress Ave" && r.MaxResults == 1),
                null,
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>(
            [
                new GeocodeCandidate(
                    "1100 Congress Ave, Austin, TX",
                    -97.7404,
                    30.2747,
                    98.5,
                    new Dictionary<string, string?>())
                {
                    SpatialReferenceWkid = 4326,
                    MatchLevel = "PointAddress"
                }
            ], "mock"));
        coordinator.ForwardGeocodeAsync(
                Arg.Is<ForwardGeocodeRequest>(r => r.Query == "nowhere at all"),
                null,
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>([], "mock"));
        coordinator.ForwardGeocodeAsync(
                Arg.Is<ForwardGeocodeRequest>(r => r.Query == "provider down st"),
                null,
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Failure<IReadOnlyList<GeocodeCandidate>>("all providers failed", "mock"));

        var response = await DispatchAsync(
            ActiveLicense(GeocodeAddressesTool.EntitlementKey),
            coordinator,
            """{"addresses":["1100 Congress Ave","nowhere at all","provider down st"]}""");

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("succeeded").GetInt32().Should().Be(1);
        structured.GetProperty("failed").GetInt32().Should().Be(2);
        structured.GetProperty("srid").GetInt32().Should().Be(4326);

        var results = structured.GetProperty("results");
        results.GetArrayLength().Should().Be(3);

        // Item 0: success, input order preserved.
        results[0].GetProperty("input").GetString().Should().Be("1100 Congress Ave");
        results[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        results[0].GetProperty("location").GetProperty("x").GetDouble().Should().BeApproximately(-97.7404, 1e-6);
        results[0].GetProperty("location").GetProperty("y").GetDouble().Should().BeApproximately(30.2747, 1e-6);
        results[0].GetProperty("location").GetProperty("srid").GetInt32().Should().Be(4326);
        results[0].GetProperty("score").GetDouble().Should().Be(98.5);
        results[0].GetProperty("matchedAddress").GetString().Should().Contain("Austin");

        // Item 1: no candidates → per-item failure without failing the batch.
        results[1].GetProperty("input").GetString().Should().Be("nowhere at all");
        results[1].GetProperty("ok").GetBoolean().Should().BeFalse();
        results[1].GetProperty("error").GetString().Should().Contain("No match");

        // Item 2: coordinator failure → per-item error message.
        results[2].GetProperty("input").GetString().Should().Be("provider down st");
        results[2].GetProperty("ok").GetBoolean().Should().BeFalse();
        results[2].GetProperty("error").GetString().Should().Contain("all providers failed");

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.Process,
            OperatorOperation.Execute,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_addresses")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddresses_OverBatchCap_ReturnsInvalidArgument()
    {
        var addresses = string.Join(',', Enumerable.Range(0, LocationToolSchemas.MaxBatchAddresses + 1)
            .Select(i => $"\"{i} Main St\""));

        var response = await DispatchAsync(
            ActiveLicense(GeocodeAddressesTool.EntitlementKey),
            Substitute.For<IGeocodeCoordinatorService>(),
            $$"""{"addresses":[{{addresses}}]}""");

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should()
            .Contain($"at most {LocationToolSchemas.MaxBatchAddresses}");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_addresses")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddresses_EmptyOrBlankAddress_ReturnsInvalidArgument()
    {
        var emptyBatch = await DispatchAsync(
            ActiveLicense(GeocodeAddressesTool.EntitlementKey),
            Substitute.For<IGeocodeCoordinatorService>(),
            """{"addresses":[]}""");
        emptyBatch!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        emptyBatch.Result!.Value.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be("invalid_argument");

        var blankItem = await DispatchAsync(
            ActiveLicense(GeocodeAddressesTool.EntitlementKey),
            Substitute.For<IGeocodeCoordinatorService>(),
            """{"addresses":["1 Main St","  "]}""");
        blankItem!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        blankItem.Result!.Value.GetProperty("structuredContent").GetProperty("message").GetString()
            .Should().Contain("addresses[1]");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_geocode_addresses")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GeocodeAddresses_WithoutBatchEntitlement_ReturnsFailedPrecondition()
    {
        var response = await DispatchAsync(
            InactiveLicense(GeocodeAddressesTool.EntitlementKey),
            Substitute.For<IGeocodeCoordinatorService>(),
            """{"addresses":["1 Main St"]}""");

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("failed_precondition");
        structured.GetProperty("message").GetString().Should().Contain(GeocodeAddressesTool.EntitlementKey);
    }

    [UnitTest]
    public void Describe_TeachesTheCsvGeocodeIngestPublishChain()
    {
        var descriptor = new GeocodeAddressesTool(_jobService, NullLogger<GeocodeAddressesTool>.Instance).Describe();

        descriptor.Name.Should().Be("honua_geocode_addresses");
        descriptor.Description.Should()
            .Contain("honua_ingest_dataset").And
            .Contain("honua_publish_service").And
            .Contain("honua_geocode_address").And
            .Contain("lon/lat");
        descriptor.OutputSchema.Should().NotBeNull();
        descriptor.Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    private async Task<McpJsonRpcResponse?> DispatchAsync(
        ILicenseEntitlementService license,
        IGeocodeCoordinatorService coordinator,
        string argumentsJson)
    {
        var services = new ServiceCollection();
        services.AddSingleton(license);
        services.AddSingleton(coordinator);

        var surface = new McpDataAccessSurface(
            [new GeocodeAddressesTool(_jobService, NullLogger<GeocodeAddressesTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services.BuildServiceProvider();

        return await surface.DispatchAsync(
            context,
            new McpJsonRpcRequest
            {
                JsonRpc = "2.0",
                Id = Json("\"batch-1\""),
                Method = "tools/call",
                Params = Json($$"""
                    {"name":"{{GeocodeAddressesTool.ToolName}}","arguments":{{argumentsJson}}}
                    """)
            },
            CancellationToken.None);
    }

    private static ILicenseEntitlementService ActiveLicense(string entitlementKey)
    {
        var license = Substitute.For<ILicenseEntitlementService>();
        license.CheckEntitlement(entitlementKey)
            .Returns(new LicenseEntitlementDecision(
                entitlementKey,
                true,
                HonuaEdition.Enterprise,
                LicenseValidationState.Valid,
                HonuaEdition.Enterprise,
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
                HonuaEdition.Enterprise,
                $"{entitlementKey} requires an active Enterprise entitlement."));
        return license;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
