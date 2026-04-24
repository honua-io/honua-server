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
    [InlineData("/ogc/tiles", HonuaTelemetry.Protocols.OgcTiles)]
    [InlineData("/ogc/processes", HonuaTelemetry.Protocols.OgcProcesses)]
    [InlineData("/ogc/services/test/wms", HonuaTelemetry.Protocols.OgcMaps)]
    [InlineData("/wfs", HonuaTelemetry.Protocols.Wfs20)]
    public void ResolveProtocol_KnownSurface_ReturnsExpectedProtocol(string path, string expectedProtocol)
    {
        RequestTelemetryClassifier.ResolveProtocol(new PathString(path)).Should().Be(expectedProtocol);
    }

    [Theory]
    [InlineData("/wfs2")]
    [InlineData("/ogc/tilesets")]
    [InlineData("/ogc/mapsheet")]
    [InlineData("/ogc/processes2")]
    [InlineData("/ogc/featuresx")]
    [InlineData("/collectionsx")]
    [InlineData("/odatax")]
    public void ResolveProtocol_PrefixWithoutSegmentBoundary_ReturnsNull(string path)
    {
        RequestTelemetryClassifier.ResolveProtocol(new PathString(path)).Should().BeNull();
    }

    [Theory]
    [InlineData("/wfs2")]
    [InlineData("/ogc/tilesets")]
    [InlineData("/ogc/mapsheet")]
    [InlineData("/ogc/processes2")]
    [InlineData("/ogc/featuresx")]
    [InlineData("/odatax")]
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
    [InlineData("/ogc/processes/processes/honua-geoprocessing/execution", "execute")]
    [InlineData("/ogc/processes/jobs/abc123/results", "jobResults")]
    public void ResolveOperation_KnownSurface_ReturnsExpectedOperation(string path, string expectedOperation)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;

        RequestTelemetryClassifier.ResolveOperation(context).Should().Be(expectedOperation);
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
