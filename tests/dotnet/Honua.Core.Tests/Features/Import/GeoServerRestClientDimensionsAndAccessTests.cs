// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoServerRestClientDimensionsAndAccessTests
{
    [Fact]
    public async Task DiscoverServiceAsync_WithFeatureTypeDimensionsAndLayerAcl_PopulatesDimensionsAndAccessRules()
    {
        const string baseUrl = "https://geoserver.example.test/geoserver/rest";
        var responses = new Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{baseUrl}/about/version.xml"] = (HttpStatusCode.OK, "application/xml", "<about><resource name=\"GeoServer\"><Version>2.26.0</Version></resource></about>"),
            [$"{baseUrl}/settings.json"] = (HttpStatusCode.OK, "application/json", """{"global":{"title":"Demo GeoServer"}}"""),
            [$"{baseUrl}/workspaces.json"] = (HttpStatusCode.OK, "application/json", """{"workspaces":{"workspace":[{"name":"demo"}]}}"""),
            [$"{baseUrl}/workspaces/demo.json"] = (HttpStatusCode.OK, "application/json", """{"workspace":{"name":"demo","default":true,"isolated":false}}"""),
            [$"{baseUrl}/workspaces/demo/datastores.json"] = (HttpStatusCode.OK, "application/json", """{"dataStores":{"dataStore":[{"name":"pg"}]}}"""),
            [$"{baseUrl}/workspaces/demo/datastores/pg.json"] = (HttpStatusCode.OK, "application/json", """{"dataStore":{"name":"pg","type":"PostGIS","enabled":true,"connectionParameters":{"entry":[{"@key":"dbtype","$":"postgis"},{"@key":"schema","$":"public"}]}}}"""),
            [$"{baseUrl}/workspaces/demo/coveragestores.json"] = (HttpStatusCode.OK, "application/json", """{"coverageStores":""}"""),
            [$"{baseUrl}/workspaces/demo/layers.json"] = (HttpStatusCode.OK, "application/json", """{"layers":{"layer":[{"name":"observations"}]}}"""),
            [$"{baseUrl}/workspaces/demo/layers/observations.json"] = (HttpStatusCode.OK, "application/json", """{"layer":{"name":"observations","nativeName":"observations","type":"VECTOR","enabled":true,"queryable":true,"srs":"EPSG:4326","resource":{"@class":"featureType","name":"demo:observations","href":"https://geoserver.example.test/geoserver/rest/workspaces/demo/datastores/pg/featuretypes/observations.json"}}}"""),
            [$"{baseUrl}/workspaces/demo/datastores/pg/featuretypes/observations.json"] = (HttpStatusCode.OK, "application/json", """
                {
                  "featureType": {
                    "name": "observations",
                    "metadata": {
                      "entry": [
                        {
                          "@key": "time",
                          "dimensionInfo": {
                            "enabled": true,
                            "attribute": "observed_at",
                            "endAttribute": "expired_at",
                            "presentation": "CONTINUOUS_INTERVAL",
                            "units": "ISO8601",
                            "defaultValue": { "strategy": "MAXIMUM" },
                            "nearestMatchEnabled": true,
                            "startValue": "2025-01-01T00:00:00Z",
                            "endValue": "2025-12-31T23:59:59Z"
                          }
                        },
                        {
                          "@key": "elevation",
                          "dimensionInfo": {
                            "enabled": true,
                            "attribute": "altitude_m",
                            "presentation": "DISCRETE_INTERVAL",
                            "resolution": 50,
                            "units": "EPSG:5030",
                            "unitSymbol": "m",
                            "defaultValue": { "strategy": "MINIMUM" },
                            "nearestMatchEnabled": true,
                            "startValue": "0",
                            "endValue": "500"
                          }
                        }
                      ]
                    }
                  }
                }
                """),
            [$"{baseUrl}/layergroups.json"] = (HttpStatusCode.OK, "application/json", """{"layerGroups":""}"""),
            [$"{baseUrl}/workspaces/demo/layergroups.json"] = (HttpStatusCode.OK, "application/json", """{"layerGroups":""}"""),
            [$"{baseUrl}/styles.json"] = (HttpStatusCode.OK, "application/json", """{"styles":""}"""),
            [$"{baseUrl}/workspaces/demo/styles.json"] = (HttpStatusCode.OK, "application/json", """{"styles":""}"""),
            [$"{baseUrl}/services/wms/settings.json"] = (HttpStatusCode.OK, "application/json", """{"wms":{"enabled":true}}"""),
            [$"{baseUrl}/services/wfs/settings.json"] = (HttpStatusCode.OK, "application/json", """{"wfs":{"enabled":true}}"""),
            [$"{baseUrl}/security/acl/layers.json"] = (HttpStatusCode.OK, "application/json", """
                {
                  "demo.observations.r": "ROLE_VIEWER,ROLE_ANALYST",
                  "demo.observations.w": "ROLE_EDITOR",
                  "demo.observations.a": "ROLE_ADMIN",
                  "*.*.r": "*",
                  "mode": "HIDE"
                }
                """)
        };

        using var handler = new StubHttpMessageHandler(responses);
        using var httpClient = new HttpClient(handler);
        var client = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var serviceInfo = await client.DiscoverServiceAsync(
            baseUrl,
            username: null,
            password: null,
            includeCompatibilityAnalysis: true,
            includeStyleContent: false,
            timeoutSeconds: 10,
            maxRetryAttempts: 1);

        var layer = serviceInfo.Layers.Should().ContainSingle().Subject;
        layer.TimeDimension.Should().NotBeNull();
        layer.TimeDimension!.Enabled.Should().BeTrue();
        layer.TimeDimension.Attribute.Should().Be("observed_at");
        layer.TimeDimension.EndAttribute.Should().Be("expired_at");
        layer.TimeDimension.Presentation.Should().Be("CONTINUOUS_INTERVAL");
        layer.TimeDimension.Units.Should().Be("ISO8601");
        layer.TimeDimension.DefaultValueStrategy.Should().Be("MAXIMUM");
        layer.TimeDimension.NearestMatchEnabled.Should().BeTrue();
        layer.TimeDimension.StartTime.Should().Be(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        layer.TimeDimension.EndTime.Should().Be(DateTimeOffset.Parse("2025-12-31T23:59:59Z", CultureInfo.InvariantCulture));

        layer.ElevationDimension.Should().NotBeNull();
        layer.ElevationDimension!.Enabled.Should().BeTrue();
        layer.ElevationDimension.Attribute.Should().Be("altitude_m");
        layer.ElevationDimension.Resolution.Should().Be(50);
        layer.ElevationDimension.Units.Should().Be("EPSG:5030");
        layer.ElevationDimension.UnitSymbol.Should().Be("m");
        layer.ElevationDimension.StartValue.Should().Be("0");
        layer.ElevationDimension.EndValue.Should().Be("500");

        serviceInfo.AccessRules.Should().HaveCount(5);
        serviceInfo.AccessRules.Should().Contain(rule =>
            rule.RuleKey == "demo.observations.r" &&
            rule.WorkspaceName == "demo" &&
            rule.LayerName == "observations" &&
            rule.Permission == "r" &&
            rule.Roles.SequenceEqual(new[] { "ROLE_VIEWER", "ROLE_ANALYST" }));
        serviceInfo.AccessRules.Should().Contain(rule =>
            rule.RuleKey == "demo.observations.w" &&
            rule.Permission == "w" &&
            rule.Roles.SequenceEqual(new[] { "ROLE_EDITOR" }));
        serviceInfo.AccessRules.Should().Contain(rule =>
            rule.RuleKey == "mode" &&
            rule.WorkspaceName == null &&
            rule.RawValue == "HIDE");
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not found", Encoding.UTF8, "text/plain")
            });
        }
    }
}
