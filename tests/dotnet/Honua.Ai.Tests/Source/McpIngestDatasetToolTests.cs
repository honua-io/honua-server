// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Coverage for <c>honua_ingest_dataset</c>: the inline CSV/GeoJSON → catalog
/// table adapter over the canonical <see cref="IFileImportService"/> pipeline.
/// Runs through the JSON-RPC <c>tools/call</c> dispatcher with the import
/// service substituted, so it validates argument validation, option plumbing,
/// per-row error projection, and the publish-chaining output triple without a
/// database.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpIngestDatasetToolTests
{
    private const string GeoJsonData =
        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-97.74,30.27]},"properties":{"name":"Capitol"}}]}""";

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestGeoJson_ReturnsPublishChainingTriple()
    {
        var importService = Substitute.For<IFileImportService>();
        ImportRequest? captured = null;
        importService.ImportFileAsync(Arg.Do<ImportRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(ImportResult.CreateSuccess(
                "landmarks",
                SupportedFileFormat.GeoJson,
                featureCount: 1,
                detectedSrid: 4326,
                physicalTableName: "imported_landmarks",
                schema: "honua_data"));

        var response = await DispatchAsync(
            BuildServices(importService),
            $$"""{"format":"geojson","data":{{JsonSerializer.Serialize(GeoJsonData)}},"datasetName":"landmarks"}""");

        var structured = AssertToolSucceeded(response);
        structured.GetProperty("success").GetBoolean().Should().BeTrue();
        structured.GetProperty("datasetName").GetString().Should().Be("landmarks");
        structured.GetProperty("rowCount").GetInt32().Should().Be(1);
        structured.GetProperty("schema").GetString().Should().Be("honua_data");
        structured.GetProperty("table").GetString().Should().Be("imported_landmarks");
        structured.GetProperty("srid").GetInt32().Should().Be(4326);
        structured.GetProperty("geometryColumn").GetString().Should().Be("geometry");
        structured.GetProperty("primaryKey").GetString().Should().Be("id");
        // No registered secure connection matches in this composition: the field
        // is omitted (null values are not written) and a warning explains why.
        structured.TryGetProperty("connectionId", out _).Should().BeFalse();
        structured.GetProperty("warnings").EnumerateArray()
            .Should().Contain(w => w.GetString()!.Contains("connectionId"));

        captured.Should().NotBeNull();
        captured!.FileName.Should().Be("landmarks.geojson");
        captured.TableName.Should().Be("landmarks");
        captured.TargetSrid.Should().Be(4326);
        captured.CsvOptions.Should().BeNull();

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.Workspace,
            OperatorOperation.Create,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestCsv_ResolvesConnectionIdWhenARegisteredConnectionMatchesTheCatalog()
    {
        var importService = Substitute.For<IFileImportService>();
        ImportRequest? captured = null;
        importService.ImportFileAsync(Arg.Do<ImportRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(ImportResult.CreateSuccess(
                "stops",
                SupportedFileFormat.Csv,
                featureCount: 2,
                detectedSrid: 4326,
                physicalTableName: "imported_stops",
                schema: "honua_data"));

        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.GetAvailableConnectionsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)["other-db", "catalog"]);
        resolver.ResolveConnectionStringAsync("other-db", Arg.Any<CancellationToken>())
            .Returns("Host=elsewhere;Database=other");
        resolver.ResolveConnectionStringAsync("catalog", Arg.Any<CancellationToken>())
            .Returns("Database=honua;Host=localhost"); // segment order differs on purpose

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua"
        }).Build();

        var response = await DispatchAsync(
            BuildServices(importService, resolver: resolver, configuration: configuration),
            $$"""
            {"format":"csv","data":"name,lon,lat\nA,-97.7,30.2\nB,-97.8,30.3\n","datasetName":"stops","longitudeColumn":"lon","latitudeColumn":"lat"}
            """);

        var structured = AssertToolSucceeded(response);
        structured.GetProperty("success").GetBoolean().Should().BeTrue();
        structured.GetProperty("connectionId").GetString().Should().Be("catalog");

        captured!.CsvOptions.Should().NotBeNull();
        captured.CsvOptions!.LongitudeColumn.Should().Be("lon");
        captured.CsvOptions.LatitudeColumn.Should().Be("lat");
        captured.CsvOptions.AddressColumn.Should().BeNull();
        captured.CsvOptions.AddressGeocoder.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestCsvWithAddressColumn_WiresTheCanonicalGeocoderIntoTheSharedPipeline()
    {
        var importService = Substitute.For<IFileImportService>();
        ImportRequest? captured = null;
        importService.ImportFileAsync(Arg.Do<ImportRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(ImportResult.CreateSuccess(
                "offices",
                SupportedFileFormat.Csv,
                featureCount: 2,
                detectedSrid: 4326,
                physicalTableName: "imported_offices",
                schema: "honua_data") with
            {
                // One row failed to geocode: the shared pipeline surfaces it per-row.
                ValidationErrors =
                [
                    ImportValidationIssue.Create(
                        ImportValidationErrorCodes.AddressGeocodeFailed,
                        "Address 'nowhere' could not be geocoded; the row was imported without geometry.",
                        featureIndex: 1)
                ]
            });

        var coordinator = Substitute.For<IGeocodeCoordinatorService>();
        coordinator.ForwardGeocodeAsync(
                Arg.Is<ForwardGeocodeRequest>(r => r.Query == "1100 Congress Ave" && r.MaxResults == 1 && r.SpatialReferenceWkid == 4326),
                null,
                Arg.Any<CancellationToken>())
            .Returns(GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>(
            [
                new GeocodeCandidate("1100 Congress Ave, Austin, TX", -97.7404, 30.2747, 98.5, new Dictionary<string, string?>())
            ], "mock"));

        var response = await DispatchAsync(
            BuildServices(
                importService,
                coordinator: coordinator,
                license: ActiveLicense(IngestDatasetTool.GeocodeEntitlementKey)),
            """
            {"format":"csv","data":"name,address\nCapitol,1100 Congress Ave\nBad,nowhere\n","datasetName":"offices","addressColumn":"address"}
            """);

        var structured = AssertToolSucceeded(response);
        structured.GetProperty("success").GetBoolean().Should().BeTrue();
        var rowError = structured.GetProperty("rowErrors").EnumerateArray().Should().ContainSingle().Subject;
        rowError.GetProperty("row").GetInt32().Should().Be(2);
        rowError.GetProperty("code").GetString().Should().Be(ImportValidationErrorCodes.AddressGeocodeFailed);
        rowError.GetProperty("message").GetString().Should().Contain("nowhere");

        // The tool must hand the shared pipeline a working geocoder hook bound to
        // the canonical coordinator, capped at the batch limit.
        captured!.CsvOptions.Should().NotBeNull();
        captured.CsvOptions!.AddressColumn.Should().Be("address");
        captured.CsvOptions.MaxGeocodedRows.Should().Be(100);
        captured.CsvOptions.AddressGeocoder.Should().NotBeNull();
        var resolved = await captured.CsvOptions.AddressGeocoder!("1100 Congress Ave", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.Longitude.Should().BeApproximately(-97.7404, 1e-6);
        resolved.Latitude.Should().BeApproximately(30.2747, 1e-6);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestOversizeInlineData_ReturnsInvalidArgumentPointingAtRestUpload()
    {
        var oversize = new string('x', IngestDatasetTool.MaxInlineDataBytes + 1);
        var response = await DispatchAsync(
            BuildServices(Substitute.For<IFileImportService>()),
            $$"""{"format":"csv","data":{{JsonSerializer.Serialize(oversize)}},"datasetName":"big"}""");

        var structured = AssertToolFailed(response);
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should()
            .Contain("/api/v1/admin/import/upload").And.Contain("inline cap");
    }

    [Theory]
    [InlineData("""{"format":"shapefile","data":"x","datasetName":"d"}""", "format")]
    [InlineData("""{"format":"csv","data":"a,b\n1,2\n","datasetName":"1bad name"}""", "datasetName")]
    [InlineData("""{"format":"csv","data":"a,b\n1,2\n","datasetName":"d","longitudeColumn":"a"}""", "together")]
    [InlineData("""{"format":"csv","data":"a,b\n1,2\n","datasetName":"d","addressColumn":"a","longitudeColumn":"a","latitudeColumn":"b"}""", "mutually exclusive")]
    [InlineData("""{"format":"geojson","data":"{}","datasetName":"d","addressColumn":"a"}""", "CSV")]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestInvalidArguments_ReturnInvalidArgument(string argumentsJson, string expectedFragment)
    {
        var response = await DispatchAsync(BuildServices(Substitute.For<IFileImportService>()), argumentsJson);

        var structured = AssertToolFailed(response);
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should().Contain(expectedFragment);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestInvalidCsv_ReturnsStructuredFailureWithImportError()
    {
        var importService = Substitute.For<IFileImportService>();
        importService.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(ImportResult.CreateFailure(
                "broken",
                SupportedFileFormat.Csv,
                "No features found in file"));

        var response = await DispatchAsync(
            BuildServices(importService),
            """{"format":"csv","data":"not really csv","datasetName":"broken"}""");

        var structured = AssertToolSucceeded(response);
        structured.GetProperty("success").GetBoolean().Should().BeFalse();
        structured.GetProperty("errorMessage").GetString().Should().Contain("No features");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestCsvOptionProblem_SurfacesAsInvalidArgument()
    {
        // The shared pipeline reports option problems (missing column, geocode row
        // cap) as import.csv_options_invalid; the tool re-shapes those into a
        // structured invalid_argument so the agent fixes the call.
        var importService = Substitute.For<IFileImportService>();
        importService.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(ImportResult.CreateFailure(
                "stops",
                SupportedFileFormat.Csv,
                "CSV header does not contain the requested longitude column 'lng_deg'.",
                errorCode: ImportValidationErrorCodes.CsvOptionsInvalid));

        var response = await DispatchAsync(
            BuildServices(importService),
            """{"format":"csv","data":"a,b\n1,2\n","datasetName":"stops","longitudeColumn":"lng_deg","latitudeColumn":"b"}""");

        var structured = AssertToolFailed(response);
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should().Contain("lng_deg");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IngestAddressColumn_WithoutEntitlementOrGeocoder_ReturnsFailedPrecondition()
    {
        // No geocoding coordinator in the composition.
        var withoutGeocoder = await DispatchAsync(
            BuildServices(Substitute.For<IFileImportService>()),
            """{"format":"csv","data":"a\nb\n","datasetName":"d","addressColumn":"a"}""");
        var noGeocoder = AssertToolFailed(withoutGeocoder);
        noGeocoder.GetProperty("code").GetString().Should().Be("failed_precondition");
        noGeocoder.GetProperty("message").GetString().Should().Contain("geocoding is not available");

        // Coordinator present but the batch entitlement is inactive.
        var withoutEntitlement = await DispatchAsync(
            BuildServices(
                Substitute.For<IFileImportService>(),
                coordinator: Substitute.For<IGeocodeCoordinatorService>(),
                license: InactiveLicense(IngestDatasetTool.GeocodeEntitlementKey)),
            """{"format":"csv","data":"a\nb\n","datasetName":"d","addressColumn":"a"}""");
        var noEntitlement = AssertToolFailed(withoutEntitlement);
        noEntitlement.GetProperty("code").GetString().Should().Be("failed_precondition");
        noEntitlement.GetProperty("message").GetString().Should().Contain(IngestDatasetTool.GeocodeEntitlementKey);
    }

    [UnitTest]
    public void Describe_TeachesThePublishChainAndFormats()
    {
        var descriptor = new IngestDatasetTool(_jobService, NullLogger<IngestDatasetTool>.Instance).Describe();

        descriptor.Name.Should().Be("honua_ingest_dataset");
        descriptor.Description.Should()
            .Contain("honua_publish_service").And
            .Contain("honua_geocode_addresses").And
            .Contain("FeatureCollection").And
            .Contain("header row").And
            .Contain("lon/lat").And
            .Contain("EPSG:4326").And
            .Contain("/api/v1/admin/import/upload");
        descriptor.OutputSchema.Should().NotBeNull();
        descriptor.Annotations!.ReadOnlyHint.Should().BeFalse();
        descriptor.Annotations.DestructiveHint.Should().BeFalse();
        descriptor.Annotations.IdempotentHint.Should().BeFalse();
    }

    private static JsonElement AssertToolSucceeded(McpJsonRpcResponse? response)
    {
        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        return result.GetProperty("structuredContent");
    }

    private static JsonElement AssertToolFailed(McpJsonRpcResponse? response)
    {
        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        return result.GetProperty("structuredContent");
    }

    private static ServiceProvider BuildServices(
        IFileImportService importService,
        IGeocodeCoordinatorService? coordinator = null,
        ILicenseEntitlementService? license = null,
        ISecureConnectionResolver? resolver = null,
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(importService);
        services.AddSingleton(license ?? ActiveLicense(IngestDatasetTool.GeocodeEntitlementKey));
        if (coordinator is not null)
        {
            services.AddSingleton(coordinator);
        }

        if (resolver is not null)
        {
            services.AddSingleton(resolver);
        }

        if (configuration is not null)
        {
            services.AddSingleton(configuration);
        }

        return services.BuildServiceProvider();
    }

    private async Task<McpJsonRpcResponse?> DispatchAsync(ServiceProvider services, string argumentsJson)
    {
        var surface = new McpDataAccessSurface(
            [new IngestDatasetTool(_jobService, NullLogger<IngestDatasetTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;

        return await surface.DispatchAsync(
            context,
            new McpJsonRpcRequest
            {
                JsonRpc = "2.0",
                Id = Json("\"ingest-1\""),
                Method = "tools/call",
                Params = Json($$"""
                    {"name":"{{IngestDatasetTool.ToolName}}","arguments":{{argumentsJson}}}
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
