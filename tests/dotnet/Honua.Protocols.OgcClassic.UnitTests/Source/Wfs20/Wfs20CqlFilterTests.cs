// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20CqlFilterTests
{
    private readonly ISqlFilterTranslator _sql = Substitute.For<ISqlFilterTranslator>();
    private FilterExpression? _translated;

    public Wfs20CqlFilterTests()
    {
        _sql.Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Object>())
            .Returns(call =>
            {
                _translated = call.Arg<FilterExpression>();
                return new SqlFragment("population > 100", []);
            });
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task GetFeature_ValidCqlFilter_PassesTranslatedSqlFilterToReader()
    {
        FeatureQuery? capturedQuery = null;
        var (handler, _) = CreateHandler(query => capturedQuery = query);

        var (status, body) = await ExecuteGetFeatureAsync(handler, cqlFilter: "population > 100");

        status.Should().Be(StatusCodes.Status200OK, body);
        body.Should().Contain("Honolulu");
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.SqlFilter.Should().NotBeNull();
        capturedQuery.Value.SqlFilter!.Sql.Should().Be("population > 100");
        _translated.Should().BeOfType<BinaryExpression>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task GetFeature_InvalidCqlFilter_ReturnsOgcExceptionReport()
    {
        var (handler, _) = CreateHandler(_ => { });

        var (status, body) = await ExecuteGetFeatureAsync(handler, cqlFilter: "population >>>");

        status.Should().Be(StatusCodes.Status400BadRequest, body);
        body.Should().Contain("ExceptionReport");
        body.Should().Contain("InvalidParameterValue");
        body.Should().Contain("cql_filter");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task GetFeature_HiddenFieldInCqlFilter_ReturnsOgcExceptionReport()
    {
        var (handler, _) = CreateHandler(_ => { });

        var (status, body) = await ExecuteGetFeatureAsync(handler, cqlFilter: "secret_code = 'abc'");

        status.Should().Be(StatusCodes.Status400BadRequest, body);
        body.Should().Contain("ExceptionReport");
        body.Should().Contain("InvalidParameterValue");
        body.Should().Contain("cql_filter");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task GetFeature_CombiningFilterAndCqlFilter_ReturnsOgcExceptionReport()
    {
        var (handler, _) = CreateHandler(_ => { });
        const string fesFilter =
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>name</fes:ValueReference><fes:Literal>Honolulu</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";

        var (status, body) = await ExecuteGetFeatureAsync(handler, filter: fesFilter, cqlFilter: "population > 100");

        status.Should().Be(StatusCodes.Status400BadRequest, body);
        body.Should().Contain("ExceptionReport");
        body.Should().Contain("InvalidParameterValue");
        body.Should().Contain("cql_filter");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public void AllowedQueryParameters_GetFeature_IncludesCqlFilter()
    {
        Wfs20Utilities.AllowedQueryParameters.GetFeature.Should().Contain("CQL_FILTER");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task LegacyGetFeature_ValidAndInvalidCqlFilter_BehavesAsExpected()
    {
        FeatureQuery? capturedQuery = null;
        var (handler, _) = CreateHandler(query => capturedQuery = query);

        await using var okServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddValidationServices()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var okContext = new DefaultHttpContext
        {
            RequestServices = okServices
        };
        okContext.Request.Scheme = "https";
        okContext.Request.Host = new HostString("example.test");
        okContext.Request.Path = "/wfs";
        okContext.Response.Body = new MemoryStream();

        var okResult = await handler.HandleLegacyGetFeatureAsync(
            okContext,
            version: "1.1.0",
            typeNames: "cities",
            outputFormat: null,
            maxFeatures: null,
            sortBy: null,
            bbox: null,
            filter: null,
            featureId: null,
            propertyName: null,
            srsName: null,
            resultType: null,
            cqlFilter: "population > 100",
            cancellationToken: CancellationToken.None);
        await okResult.ExecuteAsync(okContext);
        okContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.SqlFilter.Should().NotBeNull();

        await using var errServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddValidationServices()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var errContext = new DefaultHttpContext
        {
            RequestServices = errServices
        };
        errContext.Response.Body = new MemoryStream();
        var errResult = await handler.HandleLegacyGetFeatureAsync(
            errContext,
            version: "1.1.0",
            typeNames: "cities",
            outputFormat: null,
            maxFeatures: null,
            sortBy: null,
            bbox: null,
            filter: null,
            featureId: null,
            propertyName: null,
            srsName: null,
            resultType: null,
            cqlFilter: "population >>>",
            cancellationToken: CancellationToken.None);
        await errResult.ExecuteAsync(errContext);
        errContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        errContext.Response.Body.Position = 0;
        using var sr = new StreamReader(errContext.Response.Body, Encoding.UTF8);
        var errBody = await sr.ReadToEndAsync();
        errBody.Should().Contain("InvalidParameterValue");
    }

    private (Wfs20Handler Handler, IFeatureReader Reader) CreateHandler(Action<FeatureQuery> onQuery)
    {
        var pointWkb = new WKBWriter().Write(new Point(-157.8583, 21.3069));
        var feature = new Feature
        {
            Id = 1,
            Geometry = pointWkb,
            Attributes = ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1L)
                .Add("name", "Honolulu")
                .Add("population", 350000)
        };

        var reader = Substitute.For<IFeatureReader>();
        reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onQuery(call.Arg<FeatureQuery>());
                return Task.FromResult(1L);
            });
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onQuery(call.Arg<FeatureQuery>());
                return Task.FromResult(new QueryResult<Feature>
                {
                    Items = [feature],
                    TotalCount = 1
                });
            });

        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "res-1",
                Name = "cities",
                Title = "Cities"
            },
            Type = MetadataV2ObjectType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true }
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "secret_code", Type = MetadataV2FieldType.String, Hidden = true },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }
            ]
        };

        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-1",
                Name = "default",
                Title = "Default"
            },
            Protocols = [ServiceProtocols.Wfs20],
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true }
        };

        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "binding-1",
                Name = "binding-1"
            },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = 1,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };

        var publication = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "cities",
                Name = "cities"
            },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            LayerIndex = 1,
            ServiceLocalId = "cities",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };

        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Services = [service],
                Resources = [resource],
                StorageBindings = [binding],
                Publications = [publication]
            },
            "\"test\"",
            DateTimeOffset.UtcNow);

        IMetadataV2GraphProvider graphProvider = new StaticGraphProvider(snapshot);
        var translator = new FilterExpressionTranslator(_sql);
        var queryProcessor = new QueryProcessor(translator, reader, NullLogger<QueryProcessor>.Instance, graphProvider);

        var queryServices = new Wfs20QueryServices(
            reader,
            Substitute.For<IGmlFeatureStore>(),
            graphProvider,
            new FilterExpressionService(translator),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            queryProcessor,
            Options.Create(new Wfs20Options()),
            Substitute.For<IConfiguration>());

        var handler = Wfs20HandlerTestFactory.Create(queryServices, Substitute.For<IFeatureWriter>());
        return (handler, reader);
    }

    private static async Task<(int Status, string Body)> ExecuteGetFeatureAsync(
        Wfs20Handler handler,
        string? filter = null,
        string? cqlFilter = null)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddValidationServices()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/wfs";
        context.Response.Body = new MemoryStream();

        var result = await handler.HandleGetFeatureAsync(
            context,
            typeNames: "cities",
            outputFormat: "application/geo+json",
            count: null,
            startIndex: null,
            sortBy: null,
            bbox: null,
            filter: filter,
            resourceId: null,
            propertyName: null,
            srsName: null,
            resultType: null,
            cqlFilter: cqlFilter,
            cancellationToken: CancellationToken.None);

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class StaticGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(snapshot);
    }
}
