// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoServerRestClientDimensionsAndAccessTests
{
    private const string BaseUrl = "https://gs-core.example.test/geoserver/rest";

    [Fact]
    public async Task DiscoverServiceAsync_WithFeatureTypeDimensionsAndLayerAcl_PopulatesDimensionsAndAccessRules()
    {
        using var handler = new StubHttpMessageHandler(BuildDiscoveryResponses());
        using var httpClient = new HttpClient(handler);
        var client = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var serviceInfo = await client.DiscoverServiceAsync(
            BaseUrl,
            username: null,
            password: null,
            includeCompatibilityAnalysis: true,
            includeStyleContent: false,
            timeoutSeconds: 10,
            maxRetryAttempts: 1);

        var layer = serviceInfo.Layers.Should().ContainSingle().Subject;
        AssertTimeDimension(layer.TimeDimension);
        AssertElevationDimension(layer.ElevationDimension);
        AssertAccessRules(serviceInfo.AccessRules);
    }

    private static void AssertTimeDimension(GeoServerDimensionInfo? dimension)
    {
        dimension.Should().NotBeNull();
        dimension!.Enabled.Should().BeTrue();
        dimension.Attribute.Should().Be("captured_at");
        dimension.EndAttribute.Should().Be("valid_until");
        dimension.Presentation.Should().Be("CONTINUOUS_INTERVAL");
        dimension.Units.Should().Be("ISO8601");
        dimension.DefaultValueStrategy.Should().Be("MAXIMUM");
        dimension.NearestMatchEnabled.Should().BeTrue();
        dimension.StartTime.Should().Be(DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture));
        dimension.EndTime.Should().Be(DateTimeOffset.Parse("2024-09-30T23:59:59Z", CultureInfo.InvariantCulture));
    }

    private static void AssertElevationDimension(GeoServerDimensionInfo? dimension)
    {
        dimension.Should().NotBeNull();
        dimension!.Enabled.Should().BeTrue();
        dimension.Attribute.Should().Be("height_m");
        dimension.Resolution.Should().Be(25);
        dimension.Units.Should().Be("EPSG:5030");
        dimension.UnitSymbol.Should().Be("m");
        dimension.StartValue.Should().Be("10");
        dimension.EndValue.Should().Be("210");
    }

    private static void AssertAccessRules(GeoServerLayerAccessRule[] rules)
    {
        rules.Should().HaveCount(4);
        rules.Should().Contain(rule =>
            rule.RuleKey == "hydro.gauges.r" &&
            rule.WorkspaceName == "hydro" &&
            rule.LayerName == "gauges" &&
            rule.Permission == "r" &&
            rule.Roles.SequenceEqual(new[] { "ROLE_READER", "ROLE_HYDRO" }));
        rules.Should().Contain(rule =>
            rule.RuleKey == "hydro.gauges.w" &&
            rule.Permission == "w" &&
            rule.Roles.SequenceEqual(new[] { "ROLE_EDITOR" }));
        rules.Should().Contain(rule =>
            rule.RuleKey == "mode" &&
            rule.WorkspaceName == null &&
            rule.RawValue == "CHALLENGE");
    }

    private static Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> BuildDiscoveryResponses()
    {
        const string featureTypeJson = """
            {"featureType":{"name":"gauges","metadata":{"entry":[
              {"@key":"time","dimensionInfo":{"enabled":true,"attribute":"captured_at","endAttribute":"valid_until","presentation":"CONTINUOUS_INTERVAL","units":"ISO8601","defaultValue":{"strategy":"MAXIMUM"},"nearestMatchEnabled":true,"startValue":"2024-03-01T00:00:00Z","endValue":"2024-09-30T23:59:59Z"}},
              {"@key":"elevation","dimensionInfo":{"enabled":true,"attribute":"height_m","presentation":"DISCRETE_INTERVAL","resolution":25,"units":"EPSG:5030","unitSymbol":"m","defaultValue":{"strategy":"MINIMUM"},"nearestMatchEnabled":true,"startValue":"10","endValue":"210"}}
            ]}}}
            """;

        return new Dictionary<string, (HttpStatusCode, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{BaseUrl}/about/version.xml"] = (HttpStatusCode.OK, "application/xml", "<about><resource name=\"GeoServer\"><Version>2.26.1</Version></resource></about>"),
            [$"{BaseUrl}/settings.json"] = (HttpStatusCode.OK, "application/json", """{"global":{"title":"Hydro GeoServer"}}"""),
            [$"{BaseUrl}/workspaces.json"] = (HttpStatusCode.OK, "application/json", """{"workspaces":{"workspace":[{"name":"hydro"}]}}"""),
            [$"{BaseUrl}/workspaces/hydro.json"] = (HttpStatusCode.OK, "application/json", """{"workspace":{"name":"hydro","default":true,"isolated":false}}"""),
            [$"{BaseUrl}/workspaces/hydro/datastores.json"] = (HttpStatusCode.OK, "application/json", """{"dataStores":{"dataStore":[{"name":"pg_hydro"}]}}"""),
            [$"{BaseUrl}/workspaces/hydro/datastores/pg_hydro.json"] = (HttpStatusCode.OK, "application/json", """{"dataStore":{"name":"pg_hydro","type":"PostGIS","enabled":true,"connectionParameters":{"entry":[{"@key":"dbtype","$":"postgis"},{"@key":"schema","$":"public"}]}}}"""),
            [$"{BaseUrl}/workspaces/hydro/coveragestores.json"] = (HttpStatusCode.OK, "application/json", """{"coverageStores":""}"""),
            [$"{BaseUrl}/workspaces/hydro/layers.json"] = (HttpStatusCode.OK, "application/json", """{"layers":{"layer":[{"name":"gauges"}]}}"""),
            [$"{BaseUrl}/workspaces/hydro/layers/gauges.json"] = (HttpStatusCode.OK, "application/json", """{"layer":{"name":"gauges","nativeName":"raw_gauges_tbl","type":"VECTOR","enabled":true,"queryable":true,"srs":"EPSG:4326","resource":{"@class":"featureType","name":"hydro:gauges","href":"https://gs-core.example.test/geoserver/rest/workspaces/hydro/datastores/pg_hydro/featuretypes/gauges.json"}}}"""),
            [$"{BaseUrl}/workspaces/hydro/datastores/pg_hydro/featuretypes/gauges.json"] = (HttpStatusCode.OK, "application/json", featureTypeJson),
            [$"{BaseUrl}/layergroups.json"] = (HttpStatusCode.OK, "application/json", """{"layerGroups":""}"""),
            [$"{BaseUrl}/workspaces/hydro/layergroups.json"] = (HttpStatusCode.OK, "application/json", """{"layerGroups":""}"""),
            [$"{BaseUrl}/styles.json"] = (HttpStatusCode.OK, "application/json", """{"styles":""}"""),
            [$"{BaseUrl}/workspaces/hydro/styles.json"] = (HttpStatusCode.OK, "application/json", """{"styles":""}"""),
            [$"{BaseUrl}/services/wms/settings.json"] = (HttpStatusCode.OK, "application/json", """{"wms":{"enabled":true}}"""),
            [$"{BaseUrl}/services/wfs/settings.json"] = (HttpStatusCode.OK, "application/json", """{"wfs":{"enabled":true}}"""),
            [$"{BaseUrl}/security/acl/layers.json"] = (HttpStatusCode.OK, "application/json", """{"hydro.gauges.r":"ROLE_READER,ROLE_HYDRO","hydro.gauges.w":"ROLE_EDITOR","*.*.r":"*","mode":"CHALLENGE"}""")
        };
    }

    private sealed class StubHttpMessageHandler(
        Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> responses)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (responses.TryGetValue(url, out var stub))
            {
                return Task.FromResult(new HttpResponseMessage(stub.Status)
                {
                    Content = new StringContent(stub.Body, Encoding.UTF8, stub.ContentType)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
