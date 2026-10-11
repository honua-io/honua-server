// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerAnalyticsFilterTests
{
    [UnitTest]
    public async Task ApplyAnalyticsFilters_GeometryAndSpatialRel_SetsSpatialFilterAndKeepsWhere()
    {
        var queryServices = Substitute.For<IFeatureServerQueryServices>();
        queryServices.ResolveSridAsync(
                Arg.Any<string?>(),
                Arg.Any<GeoServicesSpatialReference?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(4326));
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(queryServices).BuildServiceProvider()
        };
        var values = new Dictionary<string, StringValues>
        {
            ["geometry"] = "{\"x\":1,\"y\":2}",
            ["geometryType"] = "esriGeometryPoint",
            ["spatialRel"] = "esriSpatialRelContains"
        };

        var (query, error) = await FeatureServerEndpoints.ApplyAnalyticsFiltersAsync(
            context,
            Layer("objectid"),
            values,
            new FeatureQuery { Where = "name = 'kept'" },
            CancellationToken.None);

        error.Should().BeNull();
        query.Where.Should().Be("name = 'kept'");
        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Value.SpatialRelationship.Should().Be(SpatialRelationship.Contains);
    }

    [UnitTest]
    public async Task ApplyAnalyticsFilters_TimeOnNonTimeAwareLayer_IsRejected()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Request = { Path = "/rest/services/test/FeatureServer/0/queryBins" },
            Response = { Body = new MemoryStream() }
        };
        var values = new Dictionary<string, StringValues>
        {
            ["time"] = "2020-01-01,2020-01-02"
        };

        var (_, error) = await FeatureServerEndpoints.ApplyAnalyticsFiltersAsync(
            context,
            Layer("objectid"),
            values,
            new FeatureQuery { Where = "1=1" },
            CancellationToken.None);

        error.Should().NotBeNull();
        await GeoServicesErrorAssertions.AssertGeoServicesErrorAsync(context, error!, 400);
    }

    [UnitTest]
    public async Task ApplyAnalyticsFilters_CustomObjectIds_UseSqlFilterInsteadOfStorageIds()
    {
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression?>(), Arg.Any<MetadataV2Resource>())
            .Returns(FilterTranslationResult.Success(null, new SqlFragment("gid = ANY(@ids)", new object?[] { 7L })));
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(filters).BuildServiceProvider()
        };
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "gid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                }
            ]
        };

        var (query, error) = await FeatureServerEndpoints.ApplyAnalyticsFiltersAsync(
            context,
            resource,
            new Dictionary<string, StringValues> { ["objectIds"] = "7" },
            new FeatureQuery(),
            CancellationToken.None);

        error.Should().BeNull();
        query.ObjectIds.Should().BeNull();
        query.SqlFilter.Should().NotBeNull();
        query.SqlFilter!.Sql.Should().Contain("gid");
    }

    [UnitTest]
    public async Task ApplyAnalyticsFilters_StorageObjectIds_UseObjectIdFastPath()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        var (query, error) = await FeatureServerEndpoints.ApplyAnalyticsFiltersAsync(
            context,
            Layer("objectid"),
            new Dictionary<string, StringValues> { ["objectIds"] = "7" },
            new FeatureQuery(),
            CancellationToken.None);

        error.Should().BeNull();
        query.SqlFilter.Should().BeNull();
        query.ObjectIds.Should().NotBeNull();
        query.ObjectIds!.Value.Should().Equal(7L);
    }

    private static MetadataV2Resource Layer(string objectIdField)
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = objectIdField, Type = MetadataV2FieldType.BigInteger }
            ]
        };
}
