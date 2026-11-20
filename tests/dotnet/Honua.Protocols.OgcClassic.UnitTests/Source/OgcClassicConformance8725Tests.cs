// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Query;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.Protocols.Ogc.Classic.Wmts;
using Honua.Protocols.Ogc.Classic.Wps20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class OgcClassicConformance8725Tests
{
    [UnitTheory]
    [InlineData("CRS:84")]
    [InlineData("CRS84")]
    [InlineData("OGC:CRS84")]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84")]
    [InlineData("urn:ogc:def:crs:OGC:2:84")]
    [InlineData("http://www.opengis.net/def/crs/OGC/1.3/CRS84")]
    public void Wms_TryParseWmsCrs_PreservesCrs84AndEastNorthAxisOrder(string crsInput)
    {
        WmsRequestHandlers.TryParseWmsCrs(crsInput, out var srid, out var normalizedCrs).Should().BeTrue();
        srid.Should().Be(4326);
        normalizedCrs.Should().Be("CRS:84");

        WmsRequestHandlers.TryParseWmsBbox("-120,30,-110,40", normalizedCrs, "1.3.0", out var extent).Should().BeTrue();
        extent.MinX.Should().Be(-120);
        extent.MinY.Should().Be(30);
        extent.MaxX.Should().Be(-110);
        extent.MaxY.Should().Be(40);
    }

    [UnitTest]
    public void Iso8601TemporalIntervalParser_SupportsCommaSeparatedListsAndThreePartIntervals()
    {
        Iso8601TemporalIntervalParser.TryParseRange(
            "2024-01-01T00:00:00Z,2024-06-15T12:00:00Z",
            out var listStart,
            out var listEnd,
            out var listError).Should().BeTrue(listError);
        listStart.Should().Be(DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        listEnd.Should().Be(DateTimeOffset.Parse("2024-06-15T12:00:00Z", CultureInfo.InvariantCulture));

        Iso8601TemporalIntervalParser.TryParseRange(
            "2024-01-01T00:00:00Z/2024-12-31T23:59:59Z/P1D",
            out var tripleStart,
            out var tripleEnd,
            out var tripleError).Should().BeTrue(tripleError);
        tripleStart.Should().Be(DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        tripleEnd.Should().Be(DateTimeOffset.Parse("2024-12-31T23:59:59Z", CultureInfo.InvariantCulture));

        Iso8601TemporalIntervalParser.TryParseRange(
            "2024-01-01T00:00:00Z/2024-12-31T23:59:59Z/0",
            out _,
            out _,
            out _).Should().BeTrue();

        Iso8601TemporalIntervalParser.TryParseRange(
            "2024-01-01T00:00:00Z/2024-12-31T23:59:59Z/PT0S",
            out _,
            out _,
            out _).Should().BeFalse();
    }

    [UnitTest]
    public void Wcs_TryParseSubsetEnvelope_AllowsEqualBoundTrimsAndRejects1DSlices()
    {
        var raster = CreateTestRaster();
        SpatialReferenceHelpers.TryParseCrsDefinition("CRS:84", out var crs).Should().BeTrue();

        var equalTrim = new StringValues(["Long(-122.4,-122.4)", "Lat(37.7,37.8)"]);
        Wcs20Handler.TryParseSubsetEnvelope(equalTrim, raster, crs, out var envelope, out _).Should().BeTrue();
        envelope.MinX.Should().BeApproximately(-122.4, 1e-9);
        envelope.MaxX.Should().BeApproximately(-122.4, 1e-9);
        envelope.MinY.Should().BeApproximately(37.7, 1e-9);
        envelope.MaxY.Should().BeApproximately(37.8, 1e-9);

        var slice = new StringValues(["Long(-122.4)"]);
        Wcs20Handler.TryParseSubsetEnvelope(slice, raster, crs, out _, out var sliceError).Should().BeFalse();
        sliceError.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidSubsetting);
    }

    [UnitTest]
    public void Wcs_TryParseBboxEnvelope_RespectsSubsettingCrsAxisOrder()
    {
        SpatialReferenceHelpers.TryParseCrsDefinition("EPSG:4326", out var epsg4326).Should().BeTrue();
        epsg4326.AxisOrder.Should().Be(AxisOrder.NorthEast);

        Wcs20Handler.TryParseBboxEnvelope("37.7,-122.4,37.8,-122.3", epsg4326, out var envelope, out _).Should().BeTrue();
        envelope.MinX.Should().BeApproximately(-122.4, 1e-9);
        envelope.MaxX.Should().BeApproximately(-122.3, 1e-9);
        envelope.MinY.Should().BeApproximately(37.7, 1e-9);
        envelope.MaxY.Should().BeApproximately(37.8, 1e-9);
    }

    [UnitTest]
    public void Wcs_RangeSubsetAndScaling_EmitDedicatedExceptionCodes()
    {
        var raster = CreateTestRaster();

        var rangeQuery = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["RANGESUBSET"] = "nonexistentBand"
        });
        Wcs20Handler.TryResolveRangeSubset(rangeQuery, raster, out _, out var rangeError).Should().BeFalse();
        rangeError.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.NoSuchField);

        var scaleFactorQuery = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["SCALEFACTOR"] = "0"
        });
        Wcs20Handler.TryResolveScaling(scaleFactorQuery, 64, 64, out _, out _, out var scaleFactorError).Should().BeFalse();
        scaleFactorError.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidScaleFactor);

        var scaleExtentQuery = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["SCALEEXTENT"] = "x(10,5)"
        });
        Wcs20Handler.TryResolveScaling(scaleExtentQuery, 64, 64, out _, out _, out var scaleExtentError).Should().BeFalse();
        scaleExtentError.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidExtent);

        var scaleAxisQuery = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["SCALEAXES"] = "z(2)"
        });
        Wcs20Handler.TryResolveScaling(scaleAxisQuery, 64, 64, out _, out _, out var scaleAxisError).Should().BeFalse();
        scaleAxisError.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidAxisLabel);
    }

    [UnitTest]
    public void Wfs_ParseTransactionGeometry_UsesActionSrsNameFallback()
    {
        var geometry = Wfs20Handler.ParseTransactionGeometry(
            XElement.Parse("""
                <gml:Point xmlns:gml="http://www.opengis.net/gml/3.2">
                  <gml:pos>-122.4 37.78</gml:pos>
                </gml:Point>
                """),
            defaultSrid: 4326,
            fallbackSrsName: "CRS:84");

        geometry.SRID.Should().Be(4326);
        geometry.Coordinate.X.Should().BeApproximately(-122.4, 1e-9);
        geometry.Coordinate.Y.Should().BeApproximately(37.78, 1e-9);
    }

    [UnitTest]
    public async Task Wfs_Dispatcher_SupportsFormUrlEncodedPostAndXmlCapabilitiesChildElements()
    {
        var handler = Wfs20HandlerTestFactory.Create(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>());

        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        // 1. XML POST GetCapabilities with child <ows:AcceptVersions><ows:Version>9.9.9</ows:Version></ows:AcceptVersions>
        var xmlCapsContext = new DefaultHttpContext { RequestServices = services };
        xmlCapsContext.Request.Method = HttpMethods.Post;
        xmlCapsContext.Request.ContentType = "application/xml";
        var xmlBody = """
            <wfs:GetCapabilities service="WFS" xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:ows="http://www.opengis.net/ows/1.1">
              <ows:AcceptVersions>
                <ows:Version>9.9.9</ows:Version>
              </ows:AcceptVersions>
            </wfs:GetCapabilities>
            """;
        xmlCapsContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(xmlBody));
        xmlCapsContext.Response.Body = new MemoryStream();

        var xmlCapsResult = await Wfs20DispatcherEndpoint.HandleWfsRequest(
            xmlCapsContext,
            handler,
            NullLogger<Wfs20Endpoints.Wfs20EndpointsLog>.Instance);
        await xmlCapsResult.ExecuteAsync(xmlCapsContext);
        var xmlCapsResponse = await ReadBodyAsync(xmlCapsContext);
        xmlCapsContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        xmlCapsResponse.Should().Contain("VersionNegotiationFailed");

        // 2. Form-urlencoded POST operational request with unsupported VERSION returns InvalidParameterValue (locator="version")
        var formContext = new DefaultHttpContext { RequestServices = services };
        formContext.Request.Method = HttpMethods.Post;
        formContext.Request.ContentType = "application/x-www-form-urlencoded";
        var formBody = "SERVICE=WFS&REQUEST=GetFeature&VERSION=9.9.9&TYPENAMES=layer1";
        formContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(formBody));
        formContext.Response.Body = new MemoryStream();

        var formResult = await Wfs20DispatcherEndpoint.HandleWfsRequest(
            formContext,
            handler,
            NullLogger<Wfs20Endpoints.Wfs20EndpointsLog>.Instance);
        await formResult.ExecuteAsync(formContext);
        var formResponse = await ReadBodyAsync(formContext);
        formContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        formResponse.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        formResponse.Should().Contain("locator=\"version\"");
    }

    [UnitTest]
    public async Task Wps_MultiIdentifierDescribeProcess_XmlAcceptVersions_AndPlainTextDataUnescaping()
    {
        var procA = new ProcessDefinition
        {
            ProcessId = "proc.a",
            Title = "Process A",
            Description = "First process",
            Category = "test",
            ExecutionKind = ProcessExecutionKind.Job,
            SupportedExecutionModes = ProcessExecutionModes.Async,
            SupportedEntryPoints = ProcessEntryPoints.Job,
            Parameters = [new ProcessParameterSpec { Name = "text", DisplayName = "Text", Description = "Text", ValueType = ProcessParameterValueType.Text, Required = true }],
            OutputArtifactKinds = []
        };
        var procB = procA with { ProcessId = "proc.b", Title = "Process B" };
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.GetProcess("proc.a").Returns(procA);
        catalog.GetProcess("proc.b").Returns(procB);
        catalog.ListProcesses().Returns([procA, procB]);

        AnalysisPlan? capturedPlan = null;
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.SubmitJobAsync(
                Arg.Do<AnalysisPlan>(p => capturedPlan = p),
                Arg.Any<string?>(),
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobRecord
            {
                OperationId = "job-1",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec { TargetKind = default, Backend = "test", Kind = ExecutionJobKind.Geoprocessing, WorkloadName = "proc.a" }
            });

        // 1. Comma-separated KVP DescribeProcess
        var kvpDescribe = await DispatchWpsAsync(
            catalog,
            jobs,
            HttpMethods.Get,
            "?service=WPS&version=2.0.0&request=DescribeProcess&identifier=proc.a,proc.b",
            null);
        kvpDescribe.StatusCode.Should().Be(StatusCodes.Status200OK);
        kvpDescribe.Body.Should().Contain("<ows:Identifier>proc.a</ows:Identifier>");
        kvpDescribe.Body.Should().Contain("<ows:Identifier>proc.b</ows:Identifier>");

        // 2. XML POST DescribeProcess with multiple <ows:Identifier> elements
        var xmlDescribe = await DispatchWpsAsync(
            catalog,
            jobs,
            HttpMethods.Post,
            string.Empty,
            """
            <wps:DescribeProcess service="WPS" version="2.0.0" xmlns:wps="http://www.opengis.net/wps/2.0" xmlns:ows="http://www.opengis.net/ows/2.0">
              <ows:Identifier>proc.a</ows:Identifier>
              <ows:Identifier>proc.b</ows:Identifier>
            </wps:DescribeProcess>
            """);
        xmlDescribe.StatusCode.Should().Be(StatusCodes.Status200OK);
        xmlDescribe.Body.Should().Contain("<ows:Identifier>proc.a</ows:Identifier>");
        xmlDescribe.Body.Should().Contain("<ows:Identifier>proc.b</ows:Identifier>");

        // 3. XML POST GetCapabilities with unsupported <ows:AcceptVersions>
        var xmlCaps = await DispatchWpsAsync(
            catalog,
            jobs,
            HttpMethods.Post,
            string.Empty,
            """
            <wps:GetCapabilities service="WPS" xmlns:wps="http://www.opengis.net/wps/2.0" xmlns:ows="http://www.opengis.net/ows/2.0">
              <ows:AcceptVersions>
                <ows:Version>9.9.9</ows:Version>
              </ows:AcceptVersions>
            </wps:GetCapabilities>
            """);
        xmlCaps.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        xmlCaps.Body.Should().Contain("exceptionCode=\"VersionNegotiationFailed\"");

        // 4. XML POST Execute with plain-text <wps:Data> containing &amp; entity
        var xmlExec = await DispatchWpsAsync(
            catalog,
            jobs,
            HttpMethods.Post,
            string.Empty,
            """
            <wps:Execute service="WPS" version="2.0.0" mode="async" response="document" xmlns:wps="http://www.opengis.net/wps/2.0" xmlns:ows="http://www.opengis.net/ows/2.0">
              <ows:Identifier>proc.a</ows:Identifier>
              <wps:Input id="text">
                <wps:Data>A &amp; B</wps:Data>
              </wps:Input>
            </wps:Execute>
            """);
        xmlExec.StatusCode.Should().Be(StatusCodes.Status201Created, xmlExec.Body);
        capturedPlan.Should().NotBeNull();
        capturedPlan!.Steps.Single().Inputs["text"].Should().Be("A & B");
    }

    [UnitTest]
    public async Task Wmts_UnknownRequest_Returns501OperationNotSupported()
    {
        var graph = BuildMinimalWmtsGraph();
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(MetadataV2GraphSnapshot.Create(graph));
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("svc1", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(
                graph.Services[0],
                MetadataV2GraphSnapshot.Create(graph)));
        var routes = Substitute.For<IRouteParameterValidator>();
        routes.ValidateServiceId(Arg.Any<HttpContext>()).Returns(ValidationResult<string>.Success("svc1"));

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(graphProvider)
            .AddSingleton(validator)
            .AddSingleton(routes)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["serviceId"] = "svc1";
        context.Request.Path = "/ogc/services/svc1/wmts";
        context.Request.QueryString = new QueryString("?SERVICE=WMTS&REQUEST=BogusOp&VERSION=1.0.0");
        context.Response.Body = new MemoryStream();

        var result = await WmtsRequestHandlers.HandleWmts(context);
        await result.ExecuteAsync(context);
        var body = await ReadBodyAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        body.Should().Contain("exceptionCode=\"OperationNotSupported\"");
    }

    private static MetadataV2Graph BuildMinimalWmtsGraph()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res1", Name = "layer1" },
            Type = MetadataV2ObjectType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            SchemaFields = [new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry }],
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            StorageBindingIds = ["bind1"]
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "bind1", Name = "bind1" },
            ResourceId = "res1",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "layer1",
            StorageLayerId = 1
        };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc1", Name = "svc1" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Protocols = [ServiceProtocols.Wmts]
        };
        var publication = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub1", Name = "layer1" },
            ServiceId = "svc1",
            ResourceId = "res1",
            StorageBindingId = "bind1",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            PublicationType = MetadataV2LayerType.EsriMapLayer,
            LayerIndex = 0,
            IsPrimary = true
        };
        return new MetadataV2Graph
        {
            Resources = [resource],
            StorageBindings = [binding],
            Services = [service],
            Publications = [publication]
        };
    }

    private static async Task<(int StatusCode, string Body)> DispatchWpsAsync(
        IProcessCatalog catalog,
        IGeoprocessingJobService jobs,
        string method,
        string queryString,
        string? xmlBody)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(queryString);
        if (xmlBody is not null)
        {
            context.Request.ContentType = "application/xml";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(xmlBody));
        }
        context.Response.Body = new MemoryStream();

        var options = Substitute.For<IOptionsMonitor<Wps20Options>>();
        options.CurrentValue.Returns(new Wps20Options());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        using var echo = new Wps20ConformanceEcho(options, environment);

        var result = await Wps20Endpoint.DispatchAsync(
            context,
            catalog,
            jobs,
            echo,
            NullLogger<Wps20Endpoint.Wps20EndpointLog>.Instance);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, await ReadBodyAsync(context));
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static RasterInfo CreateTestRaster()
        => new()
        {
            Id = 1,
            LayerId = 0,
            Name = "test-raster",
            Width = 64,
            Height = 64,
            BandCount = 3,
            Srid = 4326,
            PixelType = "32BF",
            Extent = new RasterExtent
            {
                XMin = -122.5,
                YMin = 37.7,
                XMax = -122.35,
                YMax = 37.84,
                Srid = 4326
            }
        };
}
