// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.ArcGisRest.Tests;

public class ArcGisRestQueryParametersTests
{
    private const string ServiceUrl = "https://services.arcgis.com/org/arcgis/rest/services/parcels/FeatureServer";

    [Fact]
    public void BuildFeatureQueryUrl_EncodesWherePagingAndOutFields()
    {
        var query = new FeatureQuery
        {
            Where = "STATE = 'WA'",
            OutFields = ["objectid", "name"],
            Offset = 40,
            Limit = 20,
            OutputSrid = 3857
        };

        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, query, token: null);

        Assert.Contains("/0/query?f=json", url, StringComparison.Ordinal);
        Assert.Contains("where=" + Uri.EscapeDataString("STATE = 'WA'"), url, StringComparison.Ordinal);
        Assert.Contains("outFields=" + Uri.EscapeDataString("objectid,name"), url, StringComparison.Ordinal);
        Assert.Contains("returnGeometry=true", url, StringComparison.Ordinal);
        Assert.Contains("outSR=3857", url, StringComparison.Ordinal);
        Assert.Contains("resultOffset=40", url, StringComparison.Ordinal);
        Assert.Contains("resultRecordCount=20", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFeatureQueryUrl_EmptyWhereDefaultsToOneEqualsOne()
    {
        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 1, new FeatureQuery(), token: null);

        Assert.Contains("where=" + Uri.EscapeDataString("1=1"), url, StringComparison.Ordinal);
        Assert.Contains("outFields=" + Uri.EscapeDataString("*"), url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFeatureQueryUrl_AppendsTokenWhenSupplied()
    {
        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 1, new FeatureQuery(), token: "abc-123");

        Assert.Contains("token=" + Uri.EscapeDataString("abc-123"), url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFeatureQueryUrl_EnvelopeSpatialFilter_EmitsEnvelopeGeometryAndIntersectsRel()
    {
        var spatial = SpatialFilter.Create(
            geometry: [],
            spatialRelationship: SpatialRelationship.EnvelopeIntersects,
            srid: 4326,
            isSimpleEnvelope: true,
            allowEnvelopeOnly: true,
            envelopeMinX: -10,
            envelopeMinY: -20,
            envelopeMaxX: 30,
            envelopeMaxY: 40);
        var query = new FeatureQuery { SpatialFilter = spatial };

        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, query, token: null);

        Assert.Contains("geometry=" + Uri.EscapeDataString("-10,-20,30,40"), url, StringComparison.Ordinal);
        Assert.Contains("geometryType=" + Uri.EscapeDataString("esriGeometryEnvelope"), url, StringComparison.Ordinal);
        Assert.Contains("spatialRel=" + Uri.EscapeDataString("esriSpatialRelIntersects"), url, StringComparison.Ordinal);
        Assert.Contains("inSR=4326", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFeatureQueryUrl_OrderBy_EmitsAscDescClauses()
    {
        var query = new FeatureQuery
        {
            OrderBy = [OrderByClause.Asc("name"), OrderByClause.Desc("created")]
        };

        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(ServiceUrl, 0, query, token: null);

        Assert.Contains("orderByFields=" + Uri.EscapeDataString("name ASC,created DESC"), url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountUrl_ReturnsCountOnlyParameter()
    {
        var url = ArcGisRestQueryParameters.BuildCountUrl(ServiceUrl, 0, FeatureQuery.WithWhere("X=1"), token: null);

        Assert.Contains("returnCountOnly=true", url, StringComparison.Ordinal);
        Assert.Contains("where=" + Uri.EscapeDataString("X=1"), url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentUrl_ReturnsExtentOnlyParameter()
    {
        var url = ArcGisRestQueryParameters.BuildExtentUrl(ServiceUrl, 0, new FeatureQuery { OutputSrid = 3857 }, token: null);

        Assert.Contains("returnExtentOnly=true", url, StringComparison.Ordinal);
        Assert.Contains("outSR=3857", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsUrl_ReturnsIdsOnlyParameter()
    {
        var url = ArcGisRestQueryParameters.BuildObjectIdsUrl(ServiceUrl, 0, new FeatureQuery(), token: null);

        Assert.Contains("returnIdsOnly=true", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLayerMetadataUrl_TargetsLayerRootJson()
    {
        var url = ArcGisRestQueryParameters.BuildLayerMetadataUrl(ServiceUrl, 3, token: null);

        Assert.Equal(ServiceUrl + "/3?f=json", url);
    }
}
