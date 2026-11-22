// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoServerMigrationFidelityTests
{
    private const string SldWithElseFilterAndTextSymbolizer = """
        <?xml version="1.0" encoding="UTF-8"?>
        <StyledLayerDescriptor version="1.0.0"
            xmlns="http://www.opengis.net/sld"
            xmlns:ogc="http://www.opengis.net/ogc">
          <NamedLayer>
            <Name>roads</Name>
            <UserStyle>
              <FeatureTypeStyle>
                <Rule>
                  <Name>FallbackRule</Name>
                  <ElseFilter/>
                  <LineSymbolizer>
                    <Stroke>
                      <CssParameter name="stroke">#336699</CssParameter>
                    </Stroke>
                  </LineSymbolizer>
                  <TextSymbolizer>
                    <Label><ogc:PropertyName>road_name</ogc:PropertyName></Label>
                  </TextSymbolizer>
                </Rule>
              </FeatureTypeStyle>
            </UserStyle>
          </NamedLayer>
        </StyledLayerDescriptor>
        """;

    [Fact]
    public async Task ImportConfigurationAsync_WithTimeAndElevationDimensions_CopiesDimensionsToLayerPublishRequest()
    {
        var responses = WithFeatureTypeDimensions(BuildBaseResponses());
        var publisher = new RecordingPublisher();
        using var handler = new DictionaryHttpHandler(responses);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, new ServiceStubs { Publisher = publisher.Object });

        var result = await service.ImportConfigurationAsync(BuildApplyRequest());

        result.Success.Should().BeTrue();
        var publish = publisher.Requests.Should().ContainSingle().Subject;
        publish.TemporalColumn.Should().Be("observed_at");
        publish.Temporal.Should().NotBeNull();
        publish.Temporal!.StartTimeField.Should().Be("observed_at");
        publish.Temporal.EndTimeField.Should().Be("valid_until");
        publish.Extensions.Should().NotBeNull().And.ContainKey("geoserverDimensions");
        publish.Extensions!["geoserverDimensions"].GetRawText().Should().Contain("\"attribute\":\"elev_m\"").And.Contain("\"units\":\"EPSG:5030\"");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithReadOnlyRuleAndNoWriteRule_HoldsForManualReviewInsteadOfBroadening()
    {
        var responses = BuildBaseResponses();
        responses["/geoserver/rest/security/acl/layers.json"] = """{"demo.roads.r":"ROLE_VIEWER"}""";
        var publisher = new RecordingPublisher();
        using var handler = new DictionaryHttpHandler(responses);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, new ServiceStubs { Publisher = publisher.Object });

        var result = await service.ImportConfigurationAsync(BuildApplyRequest());

        result.Success.Should().BeTrue();
        publisher.Requests.Should().BeEmpty("a read-only rule without an explicit write rule must not publish and grant write access to readers");
        result.ApplyPlan!.Steps.Should().ContainSingle(s => s.SourceId == "layer:demo:roads" && s.Disposition == "manual-review");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithRestrictiveWildcardRule_HoldsForManualReviewInsteadOfPublishingOpen()
    {
        var responses = BuildBaseResponses();
        responses["/geoserver/rest/security/acl/layers.json"] = """{"demo.*.r":"ROLE_SECRET"}""";
        var publisher = new RecordingPublisher();
        using var handler = new DictionaryHttpHandler(responses);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, new ServiceStubs { Publisher = publisher.Object });

        var result = await service.ImportConfigurationAsync(BuildApplyRequest());

        result.Success.Should().BeTrue();
        publisher.Requests.Should().BeEmpty("a restrictive wildcard rule must hold the layer for manual review rather than publishing open");
        result.ApplyPlan!.Steps.Should().ContainSingle(s => s.SourceId == "layer:demo:roads" && s.Disposition == "manual-review");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithReadWriteAndUnexpressedSecurityRules_TranslatesWithoutBroadening()
    {
        var responses = WithSecurityRules(BuildBaseResponses());
        var publisher = new RecordingPublisher();
        using var handler = new DictionaryHttpHandler(responses);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, new ServiceStubs { Publisher = publisher.Object });

        var result = await service.ImportConfigurationAsync(BuildApplyRequest());

        result.Success.Should().BeTrue();
        var publish = publisher.Requests.Should().ContainSingle().Subject;
        publish.AccessPolicy.Should().NotBeNull();
        publish.AccessPolicy!.AllowAnonymous.Should().BeFalse();
        publish.AccessPolicy.AllowAnonymousWrite.Should().BeFalse("wildcard *.*.w=* must not widen per-layer write access");
        publish.AccessPolicy.AllowedRoles.Should().BeEquivalentTo(["ROLE_ANALYST", "ROLE_READER"]);
        publish.AccessPolicy.AllowedWriteRoles.Should().BeEquivalentTo(["ROLE_EDITOR"]);
        AssertSecurityPlanReport(result.ApplyPlan!);
    }

    [Fact]
    public async Task ImportConfigurationAsync_DryRunPlan_ListsUnsupportedSldConstructsBeforeApply()
    {
        var responses = WithSldStyle(BuildBaseResponses(), SldWithElseFilterAndTextSymbolizer);
        var publisher = new RecordingPublisher();
        var converter = new StubWarningSldConverter(
            "[ElseFilter] (rule 'FallbackRule') ElseFilter has no portable MapLibre equivalent; rule will render unfiltered.");
        using var handler = new DictionaryHttpHandler(responses);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, new ServiceStubs { Publisher = publisher.Object, SldConverter = converter });

        var result = await service.ImportConfigurationAsync(BuildApplyRequest() with { DryRun = true, ApplyMode = false, ImportStyles = true });

        result.Success.Should().BeTrue();
        result.WasDryRun.Should().BeTrue();
        publisher.Requests.Should().BeEmpty("dry-run must report style diffs before apply");
        var layerStep = result.ApplyPlan!.Steps.Should().ContainSingle(s => s.SourceId == "layer:demo:roads").Subject;
        var diff = layerStep.StyleDiffs!.Should().ContainSingle(d => d.StyleName == "roads_style").Subject;
        diff.UnsupportedConstructs.Should().Contain("ElseFilter").And.Contain("TextSymbolizer");
        result.ApplyPlan.ManualReviewItems.Should().Contain(item =>
            item.SourceId == "layer:demo:roads" &&
            item.Code == GeoServerStyleDiffBuilder.StyleDiffReviewCode &&
            item.Reason.Contains("ElseFilter", StringComparison.Ordinal) &&
            item.Reason.Contains("TextSymbolizer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportConfigurationAsync_NonDryRun_RecordsDurableMigrationRunAndPortableReconciliationBundle()
    {
        var responses = WithSldStyle(WithSecurityRules(WithFeatureTypeDimensions(BuildBaseResponses())), SldWithElseFilterAndTextSymbolizer);
        var publisher = new RecordingPublisher();
        var runCatalog = new RecordingRunCatalog();
        using var handler = new DictionaryHttpHandler(responses);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, new ServiceStubs { Publisher = publisher.Object, RunCatalog = runCatalog.Object });

        var result = await service.ImportConfigurationAsync(BuildApplyRequest() with { JobId = "job7015", ImportStyles = true });

        result.Success.Should().BeTrue();
        result.MigrationRunId.Should().Be("geoserver-run-job7015");
        AssertReconciliationBundle(result, runCatalog);
    }

    private static void AssertSecurityPlanReport(MigrationApplyPlanArtifact plan)
    {
        var layerStep = plan.Steps.Should().ContainSingle(s => s.SourceId == "layer:demo:roads").Subject;
        layerStep.SecurityRules!.Should().Contain(r => r.RuleKey == "demo.roads.r" && r.Status == "translated");
        layerStep.SecurityRules.Should().Contain(r => r.RuleKey == "demo.roads.w" && r.Status == "translated");
        layerStep.SecurityRules.Should().Contain(r => r.RuleKey == "demo.roads.a" && r.Status == "unexpressed");
        layerStep.SecurityRules.Should().Contain(r => r.RuleKey == "*.*.w" && r.Status == "unexpressed");
        plan.ManualReviewItems.Should().Contain(i => i.Code == GeoServerSecurityRuleTranslator.UnexpressedRuleCode && i.Reason.Contains("demo.roads.a", StringComparison.Ordinal));
        plan.ManualReviewItems.Should().Contain(i => i.Code == GeoServerSecurityRuleTranslator.UnexpressedRuleCode && i.Reason.Contains("*.*.w", StringComparison.Ordinal));
    }

    private static void AssertReconciliationBundle(GeoServerImportResult result, RecordingRunCatalog runCatalog)
    {
        var bundle = result.ReconciliationBundle!;
        bundle.RunId.Should().Be("geoserver-run-job7015");
        bundle.PlanId.Should().Be("geoserver-plan-job7015");
        bundle.PlanDigest.Should().Be(result.ApplyPlan!.PlanFingerprint);
        bundle.Service.Should().Be("geoserver");
        bundle.SourceSnapshot.Counts["layers"].Should().Be(1);
        bundle.TargetSnapshot.Counts["layers"].Should().Be(1);
        bundle.SourceSnapshot.Schemas["layer:demo:roads"].Should().Contain("time:observed_at");
        bundle.TargetSnapshot.Schemas["layer:demo:roads"].Should().Contain("time:observed_at");
        runCatalog.StartedRuns.Should().ContainSingle(r => r.RunId == "geoserver-run-job7015" && r.SourceKind == "geoserver-rest");
        runCatalog.CompletedRuns.Should().ContainSingle(c =>
            c.RunId == "geoserver-run-job7015" &&
            c.Status == MigrationRunStatus.Succeeded &&
            c.Fingerprint != null &&
            c.Fingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
            c.Body != null &&
            c.Body.Contains("\"source_snapshot\"", StringComparison.Ordinal));
    }

    private static GeoServerImportRequest BuildApplyRequest()
        => new()
        {
            GeoServerRestUrl = "https://geoserver.example.test/geoserver/rest",
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            AutoPublishLayers = true,
            ImportStyles = false,
            RequestTimeoutSeconds = 5
        };

    private sealed record ServiceStubs
    {
        public ILayerPublishingService? Publisher { get; init; }
        public ISldStyleConverter? SldConverter { get; init; }
        public IMigrationRunCatalog? RunCatalog { get; init; }
    }

    private static GeoServerImportService CreateService(HttpClient httpClient, ServiceStubs stubs)
    {
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>();
        connectionProvider.Setup(p => p.GetConnectionString()).Returns("Host=localhost;Database=honua_test");
        var crsRegistry = new Mock<ICrsRegistry>();
        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoServerImportService>.Instance,
            sldConverter: stubs.SldConverter,
            layerPublishingService: stubs.Publisher)
        {
            MigrationRunCatalog = stubs.RunCatalog
        };
    }

    private static Dictionary<string, string> BuildBaseResponses()
        => new(StringComparer.Ordinal)
        {
            ["/geoserver/rest/about/version.xml"] = "<about><resource name=\"GeoServer\"><Version>2.28.0</Version></resource></about>",
            ["/geoserver/rest/settings.json"] = """{"global":{"title":"Demo GeoServer"}}""",
            ["/geoserver/rest/workspaces.json"] = """{"workspaces":{"workspace":[{"name":"demo"}]}}""",
            ["/geoserver/rest/workspaces/demo.json"] = """{"workspace":{"name":"demo","default":true}}""",
            ["/geoserver/rest/workspaces/demo/datastores.json"] = """{"dataStores":{"dataStore":[{"name":"pg"}]}}""",
            ["/geoserver/rest/workspaces/demo/datastores/pg.json"] = """
                {"dataStore":{"name":"pg","type":"PostGIS","connectionParameters":{"entry":[
                    {"@key":"host","$":"db.example.test"},{"@key":"database","$":"gis"},{"@key":"schema","$":"public"},{"@key":"dbtype","$":"postgis"}
                ]}}}
                """,
            ["/geoserver/rest/workspaces/demo/coveragestores.json"] = """{"coverageStores":{"coverageStore":""}}""",
            ["/geoserver/rest/workspaces/demo/layers.json"] = """{"layers":{"layer":[{"name":"roads"}]}}""",
            ["/geoserver/rest/workspaces/demo/layers/roads.json"] = """
                {"layer":{
                    "name":"roads","title":"Roads","enabled":true,"queryable":true,
                    "defaultStyle":{"name":"roads_style","workspace":"demo"},
                    "resource":{"href":"https://geoserver.example.test/geoserver/rest/workspaces/demo/datastores/pg/featuretypes/roads.json"},
                    "srs":"EPSG:4326","latLonBoundingBox":{"minx":-122.5,"miny":37.7,"maxx":-122.3,"maxy":37.8,"crs":"EPSG:4326"}
                }}
                """,
            ["/geoserver/rest/layergroups.json"] = """{"layerGroups":{"layerGroup":""}}""",
            ["/geoserver/rest/workspaces/demo/layergroups.json"] = """{"layerGroups":{"layerGroup":""}}""",
            ["/geoserver/rest/styles.json"] = """{"styles":{"style":""}}""",
            ["/geoserver/rest/workspaces/demo/styles.json"] = """{"styles":{"style":[{"name":"roads_style"}]}}""",
            ["/geoserver/rest/workspaces/demo/styles/roads_style.json"] = """{"style":{"name":"roads_style","format":"sld","filename":"roads_style.sld"}}"""
        };

    private static Dictionary<string, string> WithFeatureTypeDimensions(Dictionary<string, string> responses)
    {
        responses["/geoserver/rest/workspaces/demo/datastores/pg/featuretypes/roads.json"] = """
            {"featureType":{
                "name":"roads","nativeName":"roads","srs":"EPSG:4326",
                "metadata":{"entry":[
                    {"@key":"time","dimensionInfo":{"enabled":true,"attribute":"observed_at","endAttribute":"valid_until","presentation":"CONTINUOUS_INTERVAL","units":"ISO8601","nearestMatchEnabled":true}},
                    {"@key":"elevation","dimensionInfo":{"enabled":true,"attribute":"elev_m","presentation":"LIST","units":"EPSG:5030","unitSymbol":"m","defaultValue":{"strategy":"FIXED","referenceValue":"100"}}}
                ]}
            }}
            """;
        return responses;
    }

    private static Dictionary<string, string> WithSecurityRules(Dictionary<string, string> responses)
    {
        responses["/geoserver/rest/security/acl/layers.json"] = """
            {"demo.roads.r":"ROLE_READER, ROLE_ANALYST","demo.roads.w":"ROLE_EDITOR","demo.roads.a":"ROLE_ADMIN","*.*.w":"*"}
            """;
        return responses;
    }

    private static Dictionary<string, string> WithSldStyle(Dictionary<string, string> responses, string sldContent)
    {
        responses["/geoserver/rest/workspaces/demo/styles/roads_style.sld"] = sldContent;
        return responses;
    }

    private sealed class DictionaryHttpHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!responses.TryGetValue(path, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var contentType = path.EndsWith(".xml", StringComparison.Ordinal) || path.EndsWith(".sld", StringComparison.Ordinal)
                ? "application/xml"
                : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) });
        }
    }

    private sealed class RecordingPublisher
    {
        public List<LayerPublishRequest> Requests { get; } = [];
        public ILayerPublishingService Object { get; }

        public RecordingPublisher()
        {
            var mock = new Mock<ILayerPublishingService>();
            mock.Setup(p => p.PublishLayerAsync(It.IsAny<string>(), It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, LayerPublishRequest request, CancellationToken _) =>
                {
                    Requests.Add(request);
                    return new PublishedLayerSummary
                    {
                        StorageLayerId = 100 + Requests.Count,
                        LayerName = request.LayerName,
                        Schema = request.Schema,
                        Table = request.Table,
                        GeometryType = request.GeometryType ?? "LINESTRING",
                        Srid = request.Srid ?? 4326,
                        Enabled = request.Enabled,
                        ServiceName = request.ServiceName ?? "demo-geoserver"
                    };
                });
            Object = mock.Object;
        }
    }

    private sealed class StubWarningSldConverter(params string[] warnings) : ISldStyleConverter
    {
        public SldStyleConversionResult Convert(string sldXml)
            => new("[{\"id\":\"rule-0\",\"type\":\"line\"}]", "1.0.0", warnings, []);
    }

    private sealed class RecordingRunCatalog
    {
        public List<MigrationRunRecord> StartedRuns { get; } = [];
        public List<(string RunId, MigrationRunStatus Status, string? Fingerprint, string? Body)> CompletedRuns { get; } = [];
        public IMigrationRunCatalog Object { get; }

        public RecordingRunCatalog()
        {
            var mock = new Mock<IMigrationRunCatalog>();
            mock.Setup(c => c.RecordStartedAsync(It.IsAny<MigrationRunRecord>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MigrationRunRecord rec, CancellationToken _) =>
                {
                    StartedRuns.Add(rec);
                    return rec;
                });
            mock.Setup(c => c.RecordCompletedAsync(
                    It.IsAny<string>(), It.IsAny<MigrationRunStatus>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string runId, MigrationRunStatus status, DateTimeOffset completedAt, string? packRef, string? fp, string? body, string? note, CancellationToken _) =>
                {
                    CompletedRuns.Add((runId, status, fp, body));
                    return new MigrationRunRecord { RunId = runId, SourceKind = "geoserver-rest", Status = status, StartedAt = completedAt, CompletedAt = completedAt, EvidencePackRef = packRef, EvidencePackFingerprint = fp, StatusNote = note };
                });
            Object = mock.Object;
        }
    }
}
