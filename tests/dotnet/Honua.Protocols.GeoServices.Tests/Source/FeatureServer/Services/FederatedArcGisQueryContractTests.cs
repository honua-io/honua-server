// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.GeoServicesRest.Features.FeatureStore.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Federated ArcGIS REST query translation for where clauses and envelope relationships.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
public sealed class FederatedArcGisQueryContractTests
{
    private const string ServiceUrl = "https://services.example.com/arcgis/rest/services/parcels/FeatureServer";

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ConvertAsync_WhereOnlySqlFilter_ForwardsWhere()
    {
        var featureQuery = await AdaptAsync(where: "STATUS = 1", time: null, objectIds: null, useObjectIdsFastPath: true);

        featureQuery.SqlFilter.Should().NotBeNull();
        featureQuery.SqlFilterSupplementsWhere.Should().BeFalse();

        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, featureQuery);
        url.Should().Contain("where=" + Uri.EscapeDataString("STATUS = 1"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ConvertAsync_NullTime_DoesNotSupplementWhere()
    {
        var featureQuery = await AdaptAsync(where: "STATUS = 1", time: "null,null", objectIds: null, useObjectIdsFastPath: true);

        featureQuery.SqlFilterSupplementsWhere.Should().BeFalse();
        var act = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, featureQuery);
        act.Should().NotThrow();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ConvertAsync_ActiveTime_RejectsSqlFilterThatWhereDoesNotCarry()
    {
        var featureQuery = await AdaptAsync(
            where: "STATUS = 1",
            time: "2020-01-01T00:00:00Z",
            objectIds: null,
            useObjectIdsFastPath: true);

        featureQuery.SqlFilterSupplementsWhere.Should().BeTrue();
        var act = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, featureQuery);
        act.Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ConvertAsync_ObjectIdsOutsideFastPath_RejectsSupplementalSqlFilter()
    {
        var featureQuery = await AdaptAsync(
            where: "STATUS = 1",
            time: null,
            objectIds: [4L, 9L],
            useObjectIdsFastPath: false);

        featureQuery.SqlFilterSupplementsWhere.Should().BeTrue();
        var act = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, featureQuery);
        act.Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildFeatureQueryUrl_SqlFilterWithoutWhere_Throws()
    {
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("STATUS = 1", Array.Empty<object?>())
        };

        var act = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, query);
        act.Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildFeatureQueryUrl_EnforcedSqlFilter_Throws()
    {
        var query = new FeatureQuery
        {
            Where = "STATUS = 1",
            EnforcedSqlFilter = new SqlFragment("1 = 0", Array.Empty<object?>())
        };

        var act = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, query);
        act.Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildFeatureQueryUrl_TemporalFilter_Throws()
    {
        var query = new FeatureQuery
        {
            Where = "STATUS = 1",
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "acquired",
                PropertyType = TemporalPropertyType.DateTime
            }
        };

        var act = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, query);
        act.Should().Throw<NotSupportedException>();
    }

    [UnitTheory]
    [Operation(Operations.SpatialQuery)]
    [InlineData(SpatialRelationship.Intersects, "esriSpatialRelIntersects")]
    [InlineData(SpatialRelationship.EnvelopeIntersects, "esriSpatialRelEnvelopeIntersects")]
    [InlineData(SpatialRelationship.Contains, "esriSpatialRelContains")]
    [InlineData(SpatialRelationship.Within, "esriSpatialRelWithin")]
    [InlineData(SpatialRelationship.Crosses, "esriSpatialRelCrosses")]
    [InlineData(SpatialRelationship.Touches, "esriSpatialRelTouches")]
    [InlineData(SpatialRelationship.Overlaps, "esriSpatialRelOverlaps")]
    [InlineData(SpatialRelationship.Disjoint, "esriSpatialRelDisjoint")]
    public void BuildFeatureQueryUrl_EnvelopeSpatialRel_UsesMatchingEsriToken(
        SpatialRelationship relationship,
        string expectedRel)
    {
        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, EnvelopeQuery(relationship));

        url.Should().Contain("spatialRel=" + Uri.EscapeDataString(expectedRel));
        if (expectedRel != "esriSpatialRelIntersects")
        {
            url.Should().NotContain("spatialRel=" + Uri.EscapeDataString("esriSpatialRelIntersects"));
        }
    }

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void BuildFeatureQueryUrl_DistanceEnvelope_ThrowsInsteadOfIntersects()
    {
        var within = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(
            ServiceUrl, 0, EnvelopeQuery(SpatialRelationship.WithinDistance));
        var beyond = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(
            ServiceUrl, 0, EnvelopeQuery(SpatialRelationship.BeyondDistance));
        var equals = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(
            ServiceUrl, 0, EnvelopeQuery(SpatialRelationship.Equals));
        var missingCoordinates = () => ArcGisRestQueryParameters.BuildFeatureQueryUrl(
            ServiceUrl,
            0,
            new FeatureQuery
            {
                SpatialFilter = SpatialFilter.Create(
                    [1],
                    SpatialRelationship.WithinDistance,
                    isSimpleEnvelope: true)
            });

        within.Should().Throw<NotSupportedException>();
        beyond.Should().Throw<NotSupportedException>();
        equals.Should().Throw<NotSupportedException>();
        missingCoordinates.Should().Throw<NotSupportedException>();
    }

    private static FeatureQuery EnvelopeQuery(SpatialRelationship relationship)
        => new()
        {
            SpatialFilter = SpatialFilter.Create(
                [1, 2, 3],
                relationship,
                srid: 4326,
                isSimpleEnvelope: true,
                envelopeMinX: 0,
                envelopeMinY: 0,
                envelopeMaxX: 10,
                envelopeMaxY: 10)
        };

    private static async Task<FeatureQuery> AdaptAsync(
        string where,
        string? time,
        long[]? objectIds,
        bool useObjectIdsFastPath)
    {
        var adapter = new GeoServicesQueryParameterAdapter(NullLogger<GeoServicesQueryParameterAdapter>.Instance);
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "Parcels" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                new MetadataV2Field { Name = "status", Type = MetadataV2FieldType.String }
            ]
        };
        var request = new GeoServicesQueryRequest
        {
            Parameters = new QueryParameters
            {
                F = "json",
                Where = where,
                Time = time,
                ObjectIds = objectIds
            },
            QueryLimits = new QueryLimits
            {
                MaxRecordCount = 1000,
                DefaultRecordCount = 100
            },
            SqlFilter = new SqlFragment(where, Array.Empty<object?>()),
            UseObjectIdsFastPath = useObjectIdsFastPath
        };

        var converted = await adapter.ConvertAsync(request, resource);
        converted.IsSuccess.Should().BeTrue(converted.ErrorMessage);
        var processor = new QueryProcessor(
            Substitute.For<IFilterExpressionTranslator>(),
            Substitute.For<IFeatureReader>(),
            NullLogger<QueryProcessor>.Instance);
        var optimized = processor.OptimizeQuery(converted.Query!.Value, resource);
        return processor.ToFeatureQuery(optimized, resource) with { Where = where };
    }
}
