// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Middleware;

public sealed class RequestTelemetryClassifierTests
{
    [Theory]
    [InlineData("/rest/services/test/FeatureServer", HonuaTelemetry.Protocols.FeatureServer)]
    [InlineData("/rest/services/test/MapServer", HonuaTelemetry.Protocols.MapServer)]
    [InlineData("/rest/services/test/MapServer/WMS", HonuaTelemetry.Protocols.OgcMaps)]
    [InlineData("/rest/services/test/MapServer/WMTS", HonuaTelemetry.Protocols.OgcTiles)]
    [InlineData("/rest/services/test/ImageServer", HonuaTelemetry.Protocols.ImageServer)]
    [InlineData("/rest/services/Utilities/Geometry/GeometryServer", HonuaTelemetry.Protocols.GeometryService)]
    [InlineData("/rest/services/TestService/GPServer", HonuaTelemetry.Protocols.GPServer)]
    [InlineData("/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task", HonuaTelemetry.Protocols.PrintingTools)]
    [InlineData("/ogc/features", HonuaTelemetry.Protocols.OgcFeatures)]
    [InlineData("/ogc/maps", HonuaTelemetry.Protocols.OgcMaps)]
    [InlineData("/ogc/coverages", HonuaTelemetry.Protocols.OgcCoverages)]
    [InlineData("/ogc/tiles", HonuaTelemetry.Protocols.OgcTiles)]
    [InlineData("/ogc/processes", HonuaTelemetry.Protocols.OgcProcesses)]
    [InlineData("/ogc/services/test/wms", HonuaTelemetry.Protocols.OgcMaps)]
    [InlineData("/wfs", HonuaTelemetry.Protocols.Wfs20)]
    [InlineData("/ogc/services/test/wcs", HonuaTelemetry.Protocols.Wcs20)]
    [InlineData("/rest/services/0/ImageServer/WCS", HonuaTelemetry.Protocols.Wcs20)]
    [InlineData("/stac", HonuaTelemetry.Protocols.Stac)]
    [InlineData("/stac/search", HonuaTelemetry.Protocols.Stac)]
    [InlineData("/api/v1/tiles/pmtiles/world/42/WebMercatorQuad.pmtiles", HonuaTelemetry.Protocols.PMTiles)]
    [InlineData("/api/v1/tiles/pmtiles/abcdef", HonuaTelemetry.Protocols.PMTiles)]
    public void ResolveProtocol_KnownSurface_ReturnsExpectedProtocol(string path, string expectedProtocol)
    {
        RequestTelemetryClassifier.ResolveProtocol(new PathString(path)).Should().Be(expectedProtocol);
    }

    [Theory]
    [InlineData("/wfs2")]
    [InlineData("/wcs2")]
    [InlineData("/ogc/tilesets")]
    [InlineData("/ogc/mapsheet")]
    [InlineData("/ogc/coveragesx")]
    [InlineData("/ogc/processes2")]
    [InlineData("/ogc/featuresx")]
    [InlineData("/collectionsx")]
    [InlineData("/odatax")]
    [InlineData("/stacx")]
    public void ResolveProtocol_PrefixWithoutSegmentBoundary_ReturnsNull(string path)
    {
        RequestTelemetryClassifier.ResolveProtocol(new PathString(path)).Should().BeNull();
    }

    [Theory]
    [InlineData("/wfs2")]
    [InlineData("/wcs2")]
    [InlineData("/ogc/tilesets")]
    [InlineData("/ogc/mapsheet")]
    [InlineData("/ogc/coveragesx")]
    [InlineData("/ogc/processes2")]
    [InlineData("/ogc/featuresx")]
    [InlineData("/odatax")]
    [InlineData("/stacx")]
    public void ResolveOperation_PrefixWithoutSegmentBoundary_ReturnsNull(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;

        RequestTelemetryClassifier.ResolveOperation(context).Should().BeNull();
    }

    [Theory]
    [InlineData("/rest/services/test/MapServer/export", "export")]
    [InlineData("/rest/services/test/MapServer/identify", "identify")]
    [InlineData("/rest/services/test/ImageServer/exportImage", "exportImage")]
    [InlineData("/rest/services/test/ImageServer/computeStatisticsHistograms", "computeStatisticsHistograms")]
    [InlineData("/rest/services/Utilities/Geometry/GeometryServer/buffer", "buffer")]
    [InlineData("/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths", "areasAndLengths")]
    [InlineData("/rest/services/TestService/GPServer/geometry.buffer/submitJob", "submitJob")]
    [InlineData("/rest/services/TestService/GPServer/geometry.buffer/jobs/abc123", "jobStatus")]
    [InlineData("/rest/services/TestService/GPServer/geometry.buffer/jobs/abc123/results/outputFeatureLayer", "jobResult")]
    [InlineData("/rest/services/TestService/GPServer/geometry.buffer/jobs/abc123/cancel", "cancelJob")]
    [InlineData("/rest/services/TestService/GPServer/geometry.buffer/execute", "unknown")]
    [InlineData("/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute", "execute")]
    [InlineData("/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/abc123/results/Output_File", "jobResult")]
    [InlineData("/rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task/execute", "getLayoutTemplatesInfo")]
    [InlineData("/rest/services/test/FeatureServer/0/calculate", "edit")]
    [InlineData("/rest/services/test/FeatureServer/0/queryClusters", "queryClusters")]
    [InlineData("/rest/services/test/FeatureServer/0/spatialJoin", "spatialJoin")]
    [InlineData("/rest/services/test/FeatureServer/0/queryBufferAggregate", "queryBufferAggregate")]
    [InlineData("/rest/services/test/FeatureServer/0/queryDensity", "queryDensity")]
    [InlineData("/rest/services/test/FeatureServer/queryDomains", "queryDomains")]
    [InlineData("/rest/services/test/FeatureServer/replicas/abc123", "replicaInfo")]
    [InlineData("/ogc/features/collections/1/clusters", "queryClusters")]
    [InlineData("/ogc/features/collections/1/spatialJoin", "spatialJoin")]
    [InlineData("/ogc/features/collections/1/bufferAggregate", "queryBufferAggregate")]
    [InlineData("/ogc/features/collections/1/density", "queryDensity")]
    [InlineData("/ogc/coverages", "landing")]
    [InlineData("/ogc/coverages/conformance", "conformance")]
    [InlineData("/ogc/coverages/openapi.json", "api")]
    [InlineData("/ogc/coverages/collections", "collections")]
    [InlineData("/ogc/coverages/collections/1", "collection")]
    [InlineData("/ogc/coverages/collections/1/schema", "schema")]
    [InlineData("/ogc/coverages/collections/1/coverage", "coverage")]
    [InlineData("/ogc/processes/processes/honua-geoprocessing/execution", "execute")]
    [InlineData("/ogc/processes/jobs/abc123/results", "jobResults")]
    [InlineData("/stac", "catalog")]
    [InlineData("/stac/collections", "collections")]
    [InlineData("/stac/collections/1", "collection")]
    [InlineData("/stac/collections/1/items", "items")]
    [InlineData("/stac/collections/1/items/abc", "item")]
    [InlineData("/stac/search", "search.get")]
    public void ResolveOperation_KnownSurface_ReturnsExpectedOperation(string path, string expectedOperation)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be(expectedOperation);
    }

    [Fact]
    public void ResolveOperation_PMTilesProxyGet_ReturnsRange()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/tiles/pmtiles/world/42/WebMercatorQuad.pmtiles";

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("pmtiles.range");
    }

    [Fact]
    public void ResolveOperation_PMTilesProxyHead_ReturnsHead()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Request.Path = "/api/v1/tiles/pmtiles/world/42/WebMercatorQuad.pmtiles";

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("pmtiles.head");
    }

    [Fact]
    public void ResolveOperation_StacSearchPost_ReturnsSearchPost()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/stac/search";

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("search.post");
    }

    [Fact]
    public void ResolveOperation_MapServerWmtsRequest_UsesRequestName()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/rest/services/test/MapServer/WMTS";
        context.Request.QueryString = new QueryString("?SERVICE=WMTS&REQUEST=GetCapabilities");

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("wmts.getcapabilities");
    }

    [Fact]
    public void ResolveOperation_OgcServiceWmsRequest_UsesRequestName()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/ogc/services/test/wms";
        context.Request.QueryString = new QueryString("?SERVICE=WMS&REQUEST=GetMap");

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("wms.getmap");
    }

    [Fact]
    public void ResolveOperation_WfsRequest_UsesRequestName()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/wfs";
        context.Request.QueryString = new QueryString("?service=WFS&request=GetFeature");

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("wfs.getfeature");
    }

    [Fact]
    public void ResolveOperation_WcsRequest_UsesRequestName()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/rest/services/0/ImageServer/WCS";
        context.Request.QueryString = new QueryString("?service=WCS&request=GetCoverage");

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("wcs.getcoverage");
    }

    [Theory]
    [InlineData("$apply", "aggregate")]
    [InlineData("apply", "aggregate")]
    [InlineData("$search", "search")]
    [InlineData("search", "search")]
    public void ResolveOperation_ODataQueryOptionOperation_UsesSystemQueryOption(string optionName, string expectedOperation)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/odata/Features(0)";
        context.Request.QueryString = new QueryString($"?{optionName}=test");

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be(expectedOperation);
    }

    [Fact]
    public void ResolveOperation_EndpointProvidedOperation_UsesContextItem()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/wfs";
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "wfs.transaction";

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be("wfs.transaction");
    }
}
