// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoServerDimensionsAccessAndStyleDiffTests : IDisposable
{
    private const string BaseUrl = "https://geoserver.example.test/geoserver/rest";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var item in _disposables)
        {
            item.Dispose();
        }
    }

    private const string LossySld = """
        <?xml version="1.0" encoding="UTF-8"?>
        <StyledLayerDescriptor version="1.0.0"
          xmlns="http://www.opengis.net/sld"
          xmlns:ogc="http://www.opengis.net/ogc">
          <NamedLayer>
            <Name>observations</Name>
            <UserStyle>
              <FeatureTypeStyle>
                <Rule>
                  <Name>FallbackLabels</Name>
                  <ElseFilter/>
                  <PointSymbolizer>
                    <Graphic>
                      <Mark>
                        <WellKnownName>circle</WellKnownName>
                        <Fill><CssParameter name="fill">#0066cc</CssParameter></Fill>
                      </Mark>
                      <Size>6</Size>
                    </Graphic>
                  </PointSymbolizer>
                  <TextSymbolizer>
                    <Label><ogc:PropertyName>station_name</ogc:PropertyName></Label>
                  </TextSymbolizer>
                </Rule>
              </FeatureTypeStyle>
            </UserStyle>
          </NamedLayer>
        </StyledLayerDescriptor>
        """;

    [Fact]
    public async Task ImportConfigurationAsync_DryRun_SurfacesPerLayerStyleDiffAndUnexpressibleAccessRulesBeforeApply()
    {
        var responses = BuildCatalogResponses(includeUnexpressibleAcl: true);
        var service = CreateService(responses, sldConverter: new StubSldConverter());

        var result = await service.ImportConfigurationAsync(new GeoServerImportRequest
        {
            GeoServerRestUrl = BaseUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = true,
            ImportStyles = true,
            RequestTimeoutSeconds = 10
        });

        result.Success.Should().BeTrue();
        result.WasDryRun.Should().BeTrue();
        result.ApplyPlan.Should().NotBeNull();

        var layerStep = result.ApplyPlan!.Steps.Should()
            .ContainSingle(step => step.SourceId == "layer:demo:observations")
            .Subject;
        layerStep.StyleDiffs.Should().NotBeNull();
        layerStep.StyleDiffs!.Should().Contain(diff => diff.Construct == "ElseFilter");
        layerStep.StyleDiffs.Should().Contain(diff => diff.Construct == "TextSymbolizer");
        layerStep.ReviewCodes.Should().Contain("geoserver.style.diff");
        layerStep.ReviewCodes.Should().Contain("geoserver.access-rule.manual-review");

        result.ApplyPlan.ManualReviewItems.Should().Contain(item =>
            item.Code == "geoserver.access-rule.manual-review" &&
            item.Reason.Contains("demo.observations.a", StringComparison.Ordinal));
        result.ApplyPlan.ManualReviewItems.Should().Contain(item =>
            item.Code == "geoserver.access-rule.manual-review" &&
            item.Reason.Contains("*.*.r", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportConfigurationAsync_Apply_CopiesTimeAndElevationDimensionsAndAccessPolicyToLayerMetadata()
    {
        var responses = BuildCatalogResponses(includeUnexpressibleAcl: true);
        var publisher = new RecordingPublisher();
        var graphStore = new InMemoryMetadataGraphStore(layerId: 100);
        var service = CreateService(responses, publisher, graphStore, new StubSldConverter());

        var result = await service.ImportConfigurationAsync(new GeoServerImportRequest
        {
            GeoServerRestUrl = BaseUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            ImportStyles = true,
            AutoPublishLayers = true,
            RequestTimeoutSeconds = 10
        });

        result.Success.Should().BeTrue();
        publisher.Requests.Should().ContainSingle()
            .Which.TemporalColumn.Should().Be("observed_at");

        var savedResource = graphStore.Current.Graph.Resources.Should()
            .ContainSingle(r => r.Metadata.Id == "res-layer-100")
            .Subject;

        savedResource.Temporal.Should().NotBeNull();
        savedResource.Temporal!.StartTimeField.Should().Be("observed_at");
        savedResource.Temporal.EndTimeField.Should().Be("expired_at");
        savedResource.Temporal.Extent.Should().NotBeNull();
        savedResource.Temporal.Extent!.Start.Should().Be(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        savedResource.Temporal.Extent.End.Should().Be(DateTimeOffset.Parse("2025-12-31T23:59:59Z", CultureInfo.InvariantCulture));

        savedResource.Extensions.Should().ContainKey("wms");
        var wmsDimensions = savedResource.Extensions["wms"].GetProperty("dimensions");
        wmsDimensions.GetArrayLength().Should().Be(1);
        wmsDimensions[0].GetProperty("name").GetString().Should().Be("elevation");
        wmsDimensions[0].GetProperty("units").GetString().Should().Be("EPSG:5030");
        wmsDimensions[0].GetProperty("unitSymbol").GetString().Should().Be("m");
        wmsDimensions[0].GetProperty("extent").GetString().Should().Be("0/500/50");
        wmsDimensions[0].GetProperty("default").GetString().Should().Be("0");

        savedResource.AccessPolicy.Should().NotBeNull();
        savedResource.AccessPolicy!.AllowAnonymous.Should().BeFalse();
        savedResource.AccessPolicy.AllowAnonymousWrite.Should().BeFalse();
        savedResource.AccessPolicy.AllowedRoles.Should().Equal("ROLE_ANALYST", "ROLE_VIEWER");
        savedResource.AccessPolicy.AllowedWriteRoles.Should().Equal("ROLE_EDITOR");
    }

    [Fact]
    public void Translate_WithUnexpressibleWriteRule_DoesNotApplyBroaderGrant()
    {
        var layers = new[]
        {
            new GeoServerLayerInfo { Name = "observations", WorkspaceName = "demo" }
        };
        var rules = new[]
        {
            new GeoServerLayerAccessRule
            {
                RuleKey = "demo.observations.r",
                RawValue = "ROLE_VIEWER",
                WorkspaceName = "demo",
                LayerName = "observations",
                Permission = "r",
                Roles = ["ROLE_VIEWER"]
            },
            new GeoServerLayerAccessRule
            {
                RuleKey = "demo.observations.w",
                RawValue = "ROLE_EDITOR;cql=status=1",
                WorkspaceName = "demo",
                LayerName = "observations",
                Permission = "w",
                Roles = ["ROLE_EDITOR;cql=status=1"]
            },
            new GeoServerLayerAccessRule
            {
                RuleKey = "demo.*.r",
                RawValue = "*",
                WorkspaceName = "demo",
                LayerName = "*",
                Permission = "r",
                Roles = ["*"]
            }
        };

        var translated = GeoServerAccessRuleTranslator.Translate(rules, layers);

        translated.LayerPoliciesBySourceId.Should().NotContainKey("layer:demo:observations");
        translated.UnexpressibleReviewItems.Should().HaveCount(2);
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "layer:demo:observations");
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "access-rule:demo.*.r");
    }

    [Fact]
    public void Translate_WithWildcardWriteRestrictionAndPerLayerReadOnly_FailsClosedIntoManualReview()
    {
        var layers = new[]
        {
            new GeoServerLayerInfo { Name = "observations", WorkspaceName = "demo" }
        };
        var rules = new[]
        {
            new GeoServerLayerAccessRule
            {
                RuleKey = "demo.observations.r",
                RawValue = "ROLE_VIEWER",
                WorkspaceName = "demo",
                LayerName = "observations",
                Permission = "r",
                Roles = ["ROLE_VIEWER"]
            },
            new GeoServerLayerAccessRule
            {
                RuleKey = "demo.*.w",
                RawValue = "ROLE_ADMIN",
                WorkspaceName = "demo",
                LayerName = "*",
                Permission = "w",
                Roles = ["ROLE_ADMIN"]
            }
        };

        var translated = GeoServerAccessRuleTranslator.Translate(rules, layers);

        translated.LayerPoliciesBySourceId.Should().NotContainKey("layer:demo:observations");
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "access-rule:demo.*.w");
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "layer:demo:observations");
    }

    [Fact]
    public void BuildWmsDimensionsExtension_WithoutTemporalColumn_EmitsDeclaredWmsTimeDimension()
    {
        var timeDimension = new GeoServerDimensionInfo
        {
            Enabled = true,
            Presentation = "CONTINUOUS_INTERVAL",
            Units = "ISO8601",
            DefaultValueStrategy = "MAXIMUM",
            NearestMatchEnabled = true,
            StartTime = DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            EndTime = DateTimeOffset.Parse("2025-06-01T00:00:00Z", CultureInfo.InvariantCulture)
        };

        var extension = GeoServerImportService.BuildWmsDimensionsExtension(timeDimension, elevationDimension: null);

        extension.Should().NotBeNull();
        var dims = extension!.Value.GetProperty("dimensions");
        dims.GetArrayLength().Should().Be(1);
        dims[0].GetProperty("name").GetString().Should().Be("time");
        dims[0].GetProperty("units").GetString().Should().Be("ISO8601");
        dims[0].GetProperty("extent").GetString().Should().Be("2025-01-01T00:00:00Z/2025-06-01T00:00:00Z/0");
        dims[0].GetProperty("default").GetString().Should().Be("2025-06-01T00:00:00Z");
    }

    private static Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> BuildCatalogResponses(bool includeUnexpressibleAcl)
    {
        var aclJson = includeUnexpressibleAcl
            ? """
              {
                "demo.observations.r": "ROLE_VIEWER,ROLE_ANALYST",
                "demo.observations.w": "ROLE_EDITOR",
                "demo.observations.a": "ROLE_ADMIN",
                "*.*.r": "*",
                "mode": "HIDE"
              }
              """
            : """{"demo.observations.r":"ROLE_VIEWER"}""";

        return new Dictionary<string, (HttpStatusCode, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{BaseUrl}/about/version.xml"] = (HttpStatusCode.OK, "application/xml", "<about><resource name=\"GeoServer\"><Version>2.26.0</Version></resource></about>"),
            [$"{BaseUrl}/settings.json"] = (HttpStatusCode.OK, "application/json", """{"global":{"title":"Demo GeoServer"}}"""),
            [$"{BaseUrl}/workspaces.json"] = (HttpStatusCode.OK, "application/json", """{"workspaces":{"workspace":[{"name":"demo"}]}}"""),
            [$"{BaseUrl}/workspaces/demo.json"] = (HttpStatusCode.OK, "application/json", """{"workspace":{"name":"demo","default":true,"isolated":false}}"""),
            [$"{BaseUrl}/workspaces/demo/datastores.json"] = (HttpStatusCode.OK, "application/json", """{"dataStores":{"dataStore":[{"name":"pg"}]}}"""),
            [$"{BaseUrl}/workspaces/demo/datastores/pg.json"] = (HttpStatusCode.OK, "application/json", """{"dataStore":{"name":"pg","type":"PostGIS","enabled":true,"connectionParameters":{"entry":[{"@key":"dbtype","$":"postgis"},{"@key":"schema","$":"public"}]}}}"""),
            [$"{BaseUrl}/workspaces/demo/coveragestores.json"] = (HttpStatusCode.OK, "application/json", """{"coverageStores":""}"""),
            [$"{BaseUrl}/workspaces/demo/layers.json"] = (HttpStatusCode.OK, "application/json", """{"layers":{"layer":[{"name":"observations"}]}}"""),
            [$"{BaseUrl}/workspaces/demo/layers/observations.json"] = (HttpStatusCode.OK, "application/json", """{"layer":{"name":"observations","nativeName":"observations","type":"VECTOR","enabled":true,"queryable":true,"srs":"EPSG:4326","defaultStyle":{"name":"demo:obs_style"},"resource":{"@class":"featureType","name":"demo:observations","href":"https://geoserver.example.test/geoserver/rest/workspaces/demo/datastores/pg/featuretypes/observations.json"}}}"""),
            [$"{BaseUrl}/workspaces/demo/datastores/pg/featuretypes/observations.json"] = (HttpStatusCode.OK, "application/json", """
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
            [$"{BaseUrl}/layergroups.json"] = (HttpStatusCode.OK, "application/json", """{"layerGroups":""}"""),
            [$"{BaseUrl}/workspaces/demo/layergroups.json"] = (HttpStatusCode.OK, "application/json", """{"layerGroups":""}"""),
            [$"{BaseUrl}/styles.json"] = (HttpStatusCode.OK, "application/json", """{"styles":""}"""),
            [$"{BaseUrl}/workspaces/demo/styles.json"] = (HttpStatusCode.OK, "application/json", """{"styles":{"style":[{"name":"obs_style"}]}}"""),
            [$"{BaseUrl}/workspaces/demo/styles/obs_style.json"] = (HttpStatusCode.OK, "application/json", """{"style":{"name":"obs_style","format":"sld","filename":"obs_style.sld"}}"""),
            [$"{BaseUrl}/workspaces/demo/styles/obs_style.sld"] = (HttpStatusCode.OK, "application/vnd.ogc.sld+xml", LossySld),
            [$"{BaseUrl}/services/wms/settings.json"] = (HttpStatusCode.OK, "application/json", """{"wms":{"enabled":true}}"""),
            [$"{BaseUrl}/services/wfs/settings.json"] = (HttpStatusCode.OK, "application/json", """{"wfs":{"enabled":true}}"""),
            [$"{BaseUrl}/security/acl/layers.json"] = (HttpStatusCode.OK, "application/json", aclJson)
        };
    }

    private GeoServerImportService CreateService(
        Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> responses,
        ILayerPublishingService? publisher = null,
        IMetadataV2GraphStore? metadataGraphStore = null,
        ISldStyleConverter? sldConverter = null)
    {
        var handler = new StubHandler(responses);
        _disposables.Add(handler);
        var httpClient = new HttpClient(handler);
        _disposables.Add(httpClient);
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var connectionProvider = new Mock<IDatabaseConnectionProvider>();
        connectionProvider.Setup(static p => p.GetConnectionString()).Returns("Host=localhost;Database=honua_test");

        var crsRegistry = new Mock<ICrsRegistry>();
        crsRegistry
            .Setup(static r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string input, CancellationToken _) => new CrsResolutionResult
            {
                NormalizedAuthorityCode = input,
                Srid = 4326
            });

        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoServerImportService>.Instance,
            sldConverter: sldConverter,
            layerPublishingService: publisher,
            metadataGraphStore: metadataGraphStore);
    }

    private sealed class StubSldConverter : ISldStyleConverter
    {
        public SldStyleConversionResult Convert(string sldXml)
            => new(
                MapLibreLayersJson: "[]",
                DetectedSldVersion: "1.0.0",
                Warnings: ["[ElseFilter] (rule 'FallbackLabels') ElseFilter has no portable MapLibre equivalent; rule will render unfiltered."],
                Errors: []);
    }

    private sealed class StubHandler(Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> responses)
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

    private sealed class RecordingPublisher : ILayerPublishingService
    {
        public List<LayerPublishRequest> Requests { get; } = [];

        public Task<PublishedLayerSummary> PublishLayerAsync(string connectionString, LayerPublishRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new PublishedLayerSummary
            {
                StorageLayerId = 100,
                LayerName = request.LayerName,
                Schema = request.Schema,
                Table = request.Table,
                GeometryType = "POINT",
                Srid = request.Srid ?? 4326,
                Enabled = request.Enabled,
                ServiceName = request.ServiceName ?? "default"
            });
        }

        public Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(string connectionString, int layerId, string serviceName, bool enabled, CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<TablePublishValidationResult> ValidateTableForPublishAsync(string connectionString, TablePublishValidationRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<LayerExtentRefreshResult> RefreshServiceExtentsAsync(string connectionString, string serviceName, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MaterializedFeatureRefreshResult> RefreshMaterializedLayerAsync(string connectionString, int layerId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(string connectionString, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<PublishedLayerSummary?> GetPublishedLayerAsync(string connectionString, int layerId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> UnpublishLayerAsync(string connectionString, int layerId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class InMemoryMetadataGraphStore : IMetadataV2GraphStore
    {
        public MetadataV2GraphSnapshot Current { get; private set; }

        public InMemoryMetadataGraphStore(int layerId)
        {
            var graph = new MetadataV2Graph
            {
                Resources =
                [
                    new MetadataV2Object
                    {
                        Metadata = new MetadataV2ObjectMetadata
                        {
                            Id = $"res-layer-{layerId}",
                            Name = "observations"
                        }
                    }
                ]
            };
            Current = new MetadataV2GraphSnapshot(graph, "etag-1", 1, DateTimeOffset.UtcNow);
        }

        public Task<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task<MetadataV2GraphSnapshot> SaveAsync(MetadataV2Graph graph, string? expectedEtag, CancellationToken cancellationToken = default)
        {
            if (expectedEtag != null && !string.Equals(expectedEtag, Current.Etag, StringComparison.Ordinal))
            {
                throw new MetadataV2GraphConcurrencyException("Conflict", expectedEtag, Current.Etag);
            }

            Current = new MetadataV2GraphSnapshot(graph, $"etag-{Current.Revision + 1}", Current.Revision + 1, DateTimeOffset.UtcNow);
            return Task.FromResult(Current);
        }

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(long revision, string? expectedCurrentEtag, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
