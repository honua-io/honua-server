// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for migration evidence admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class MigrationEvidenceEndpointsTests : IAsyncLifetime
{
    private const string ValidTargetBaseUrl = "https://example.com";
    private readonly WebAppFixture _fixture;
    private readonly FakeMigrationEvidenceGenerator _generator;
    private HttpClient _client = null!;

    public MigrationEvidenceEndpointsTests()
    {
        _generator = new FakeMigrationEvidenceGenerator(BuildCompletedReportAsync);
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", ValidTargetBaseUrl))
            .ReplaceService<IMigrationEvidenceGenerator>(_generator);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migrations/reports")]
    public async Task StartReport_WithInvalidSourceServiceUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", new
        {
            provider = "arcgis-geoservices",
            sourceServiceUrl = "http://example.com/arcgis/rest/services/Test/FeatureServer",
            targetBaseUrl = "https://example.com",
            targetServiceName = "test",
            layers = new[]
            {
                new { sourceLayerId = 0, targetLayerId = 0 }
            },
            cutoverProfile = "pilot",
            rollbackPlanReference = "runbook://rollback/pilot"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("valid HTTPS");
    }

    [Endpoint("POST /api/v1/admin/migrations/reports")]
    [Theory]
    [InlineData(0, 50, 30, "SampleRowCount must be between 1 and 100.")]
    [InlineData(101, 50, 30, "SampleRowCount must be between 1 and 100.")]
    [InlineData(25, 0, 30, "QueryPageSize must be between 1 and 100.")]
    [InlineData(25, 101, 30, "QueryPageSize must be between 1 and 100.")]
    [InlineData(25, 50, 0, "ProbeTimeoutSeconds must be between 1 and 60.")]
    [InlineData(25, 50, 61, "ProbeTimeoutSeconds must be between 1 and 60.")]
    public async Task StartReport_WithOutOfRangeProbeControls_ReturnsBadRequest(
        int sampleRowCount,
        int queryPageSize,
        int probeTimeoutSeconds,
        string expectedError)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", new
        {
            provider = "arcgis-geoservices",
            sourceServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            targetBaseUrl = "https://example.com",
            targetServiceName = "test",
            layers = new[]
            {
                new { sourceLayerId = 0, targetLayerId = 0 }
            },
            cutoverProfile = "pilot",
            rollbackPlanReference = "runbook://rollback/pilot",
            sampleRowCount,
            queryPageSize,
            probeTimeoutSeconds
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(expectedError);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migrations/reports")]
    public async Task StartReport_WithTargetBaseUrlForAnotherServer_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", new
        {
            provider = "arcgis-geoservices",
            sourceServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            targetBaseUrl = "https://example.org",
            targetServiceName = "test",
            layers = new[]
            {
                new { sourceLayerId = 0, targetLayerId = 0 }
            },
            cutoverProfile = "pilot",
            rollbackPlanReference = "runbook://rollback/pilot"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Cross-instance migration evidence generation");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migrations/reports")]
    [Endpoint("GET /api/v1/admin/migrations/reports/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/migrations/reports")]
    [Endpoint("GET /api/v1/admin/migrations/reports/{reportId}")]
    public async Task StartReport_WithValidRequest_PersistsAndFetchesArtifact()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", CreateRequestPayload(summary: "pilot evidence"));
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var jobId = await GetStringPropertyAsync(startResponse, "jobId");
        var completedJob = await WaitForJobStatusAsync(_client, jobId, "completed", TimeSpan.FromSeconds(15));
        var reportId = completedJob.RootElement.GetProperty("reportId").GetGuid();
        completedJob.RootElement.GetProperty("readiness").GetString().Should().Be("pilot_ready");

        var listResponse = await _client.GetAsync("/api/v1/admin/migrations/reports");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDocument = await listResponse.Content.ReadFromJsonAsync<JsonDocument>();
        listDocument.Should().NotBeNull();
        var listedReport = listDocument!.RootElement.GetProperty("reports").EnumerateArray()
            .Single(report => report.GetProperty("reportId").GetGuid() == reportId);
        listedReport.GetProperty("summary").GetString().Should().Be("pilot evidence");
        listedReport.GetProperty("provider").GetString().Should().Be("arcgis-geoservices");
        listedReport.GetProperty("readiness").GetString().Should().Be("pilot_ready");

        var reportResponse = await _client.GetAsync($"/api/v1/admin/migrations/reports/{reportId}");
        reportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reportDocument = await reportResponse.Content.ReadFromJsonAsync<JsonDocument>();
        reportDocument.Should().NotBeNull();
        var root = reportDocument!.RootElement;
        root.GetProperty("request").GetProperty("sampleRowCount").GetInt32().Should().Be(25);
        root.GetProperty("request").GetProperty("queryPageSize").GetInt32().Should().Be(50);
        root.GetProperty("request").GetProperty("probeTimeoutSeconds").GetInt32().Should().Be(30);
        root.GetProperty("sourceBaseline").GetProperty("serviceUrl").GetString().Should().Be("https://example.com/arcgis/rest/services/Test/FeatureServer");
        root.GetProperty("targetSnapshot").GetProperty("serviceName").GetString().Should().Be("test");
        root.GetProperty("comparison").GetProperty("capability").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("comparison").GetProperty("style").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("comparison").GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("comparison").GetProperty("operationalReadiness").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("cutoverReadiness").GetProperty("state").GetString().Should().Be("pilot_ready");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migrations/reports/jobs/{jobId}/cancel")]
    [Endpoint("GET /api/v1/admin/migrations/reports/jobs/{jobId}")]
    public async Task CancelJob_WithRunningJob_CancelsAndSkipsPersistence()
    {
        _generator.Delay = TimeSpan.FromSeconds(5);

        try
        {
            var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", CreateRequestPayload(summary: "cancelled evidence"));
            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var jobId = await GetStringPropertyAsync(startResponse, "jobId");
            var cancelResponse = await _client.PostAsync($"/api/v1/admin/migrations/reports/jobs/{jobId}/cancel", null);
            cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await cancelResponse.Content.ReadAsStringAsync()).Should().Contain("cancellation requested");

            using var cancelledJob = await WaitForJobStatusAsync(_client, jobId, "cancelled", TimeSpan.FromSeconds(15));
            cancelledJob.RootElement.GetProperty("status").GetString().Should().Be("cancelled");

            var listResponse = await _client.GetAsync("/api/v1/admin/migrations/reports");
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var listDocument = await listResponse.Content.ReadFromJsonAsync<JsonDocument>();
            listDocument.Should().NotBeNull();
            listDocument!.RootElement.GetProperty("reports").GetArrayLength().Should().Be(0);
        }
        finally
        {
            _generator.Delay = TimeSpan.Zero;
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    [Endpoint("GET /api/v1/admin/migrations/reports/jobs/{jobId}")]
    public async Task CancelOperation_WithRunningMigrationEvidenceJob_CancelsAndSkipsPersistence()
    {
        _generator.Delay = TimeSpan.FromSeconds(5);

        try
        {
            var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", CreateRequestPayload(summary: "operations cancel evidence"));
            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var jobId = await GetStringPropertyAsync(startResponse, "jobId");
            var cancelResponse = await _client.PostAsync($"/api/v1/admin/operations/{jobId}/cancel", null);
            cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await cancelResponse.Content.ReadAsStringAsync()).Should().Contain("cancellation requested");

            using var cancelledJob = await WaitForJobStatusAsync(_client, jobId, "cancelled", TimeSpan.FromSeconds(15));
            cancelledJob.RootElement.GetProperty("status").GetString().Should().Be("cancelled");

            var listResponse = await _client.GetAsync("/api/v1/admin/migrations/reports");
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var listDocument = await listResponse.Content.ReadFromJsonAsync<JsonDocument>();
            listDocument.Should().NotBeNull();
            listDocument!.RootElement.GetProperty("reports").GetArrayLength().Should().Be(0);
        }
        finally
        {
            _generator.Delay = TimeSpan.Zero;
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migrations/reports")]
    [Endpoint("POST /api/v1/admin/migrations/reports/jobs/{jobId}/cancel")]
    [Endpoint("GET /api/v1/admin/migrations/reports/jobs/{jobId}")]
    public async Task CancelJob_AfterGenerationFinishesBeforePersistence_CancelsWithoutStoringArtifact()
    {
        var blockingStore = new BlockingMigrationEvidenceReportStore();
        var isolatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", ValidTargetBaseUrl))
            .ReplaceService<IMigrationEvidenceGenerator>(new FakeMigrationEvidenceGenerator(BuildCompletedReportAsync))
            .ReplaceService<IMigrationEvidenceReportStore>(blockingStore);

        try
        {
            await isolatedFixture.InitializeAsync();
            var client = isolatedFixture.Client;

            var startResponse = await client.PostAsJsonAsync("/api/v1/admin/migrations/reports", CreateRequestPayload(summary: "late cancel evidence"));
            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var jobId = await GetStringPropertyAsync(startResponse, "jobId");
            await blockingStore.WaitForStoreAttemptAsync(TimeSpan.FromSeconds(15));

            var cancelResponse = await client.PostAsync($"/api/v1/admin/migrations/reports/jobs/{jobId}/cancel", null);
            cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await cancelResponse.Content.ReadAsStringAsync()).Should().Contain("cancellation requested");

            using var cancelledJob = await WaitForJobStatusAsync(client, jobId, "cancelled", TimeSpan.FromSeconds(15));
            cancelledJob.RootElement.GetProperty("status").GetString().Should().Be("cancelled");
            blockingStore.StoredReports.Should().BeEmpty();
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migrations/reports")]
    [Endpoint("POST /api/v1/admin/migrations/reports/jobs/{jobId}/cancel")]
    [Endpoint("GET /api/v1/admin/migrations/reports/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/migrations/reports/{reportId}")]
    public async Task CancelJob_AfterReportPersists_ReturnsConflictAndKeepsCompletedArtifact()
    {
        var lifecycleObserver = new BlockingMigrationEvidenceLifecycleObserver();
        var isolatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", ValidTargetBaseUrl))
            .ReplaceService<IMigrationEvidenceGenerator>(new FakeMigrationEvidenceGenerator(BuildCompletedReportAsync))
            .ReplaceService<IMigrationEvidenceLifecycleObserver>(lifecycleObserver);

        try
        {
            await isolatedFixture.InitializeAsync();
            var client = isolatedFixture.Client;

            var startResponse = await client.PostAsJsonAsync("/api/v1/admin/migrations/reports", CreateRequestPayload(summary: "persisted evidence"));
            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var jobId = await GetStringPropertyAsync(startResponse, "jobId");
            await lifecycleObserver.WaitForPersistenceAsync(TimeSpan.FromSeconds(15));

            var cancelResponse = await client.PostAsync($"/api/v1/admin/migrations/reports/jobs/{jobId}/cancel", null);
            cancelResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await cancelResponse.Content.ReadAsStringAsync()).Should().Contain("no longer cancellable");

            lifecycleObserver.Release();

            using var completedJob = await WaitForJobStatusAsync(client, jobId, "completed", TimeSpan.FromSeconds(15));
            var reportId = completedJob.RootElement.GetProperty("reportId").GetGuid();

            var reportResponse = await client.GetAsync($"/api/v1/admin/migrations/reports/{reportId}");
            reportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            lifecycleObserver.Release();
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migrations/reports")]
    public async Task ListReports_WithZeroLimit_ReturnsEmptyPage()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/migrations/reports", CreateRequestPayload(summary: "zero limit evidence"));
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var jobId = await GetStringPropertyAsync(startResponse, "jobId");
        using var completedJob = await WaitForJobStatusAsync(_client, jobId, "completed", TimeSpan.FromSeconds(15));

        var listResponse = await _client.GetAsync("/api/v1/admin/migrations/reports?limit=0");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listDocument = await listResponse.Content.ReadFromJsonAsync<JsonDocument>();
        listDocument.Should().NotBeNull();
        listDocument!.RootElement.GetProperty("limit").GetInt32().Should().Be(0);
        listDocument.RootElement.GetProperty("reports").GetArrayLength().Should().Be(0);
    }

    private static object CreateRequestPayload(string summary) => new
    {
        provider = "arcgis-geoservices",
        sourceServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
        targetBaseUrl = ValidTargetBaseUrl,
        targetServiceName = "test",
        layers = new[]
        {
            new { sourceLayerId = 0, targetLayerId = 0 }
        },
        cutoverProfile = "pilot",
        rollbackPlanReference = "runbook://rollback/pilot",
        requestedBy = "integration-test",
        summary
    };

    private static async Task<string> GetStringPropertyAsync(HttpResponseMessage response, string propertyName)
    {
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return document!.RootElement.GetProperty(propertyName).GetString()!;
    }

    private static async Task<JsonDocument> WaitForJobStatusAsync(
        HttpClient client,
        string jobId,
        string expectedStatus,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/v1/admin/migrations/reports/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
            payload.Should().NotBeNull();
            if (payload!.RootElement.GetProperty("status").GetString() == expectedStatus)
            {
                return payload;
            }

            payload.Dispose();
            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for migration evidence job '{jobId}' to reach status '{expectedStatus}'.");
    }

    private static Task<MigrationEvidenceReport> BuildCompletedReportAsync(
        MigrationEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new MigrationEvidenceReport
        {
            ReportId = Guid.NewGuid(),
            SchemaVersion = "migration-evidence/v1",
            GeneratedAt = DateTimeOffset.UtcNow,
            ReportHash = "abc123def456",
            Request = request,
            SourceBaseline = new MigrationEvidenceSourceBaseline
            {
                ServiceUrl = request.SourceServiceUrl,
                ServiceName = "SourceService",
                Version = "11.3",
                Capabilities = ["Query", "Extract"],
                SupportedQueryFormats = ["json", "geojson"],
                ServiceDigest = "source-digest",
                Layers =
                [
                    new MigrationEvidenceLayerSnapshot
                    {
                        LayerId = 0,
                        Name = "Parcels",
                        GeometryType = "esriGeometryPolygon",
                        SpatialReferenceWkid = 4326,
                        FeatureCount = 25,
                        HasAttachments = false,
                        Fields =
                        [
                            new MigrationEvidenceFieldSnapshot
                            {
                                Name = "objectid",
                                CanonicalName = "objectid",
                                Type = "esriFieldTypeOID",
                                Nullable = false
                            }
                        ],
                        Extent = new MigrationEvidenceExtentSnapshot
                        {
                            MinX = -157.9,
                            MinY = 21.2,
                            MaxX = -157.7,
                            MaxY = 21.4,
                            SpatialReferenceWkid = 4326
                        },
                        LayerDigest = "source-layer-digest",
                        StyleDigest = "source-style-digest"
                    }
                ]
            },
            TargetSnapshot = new MigrationEvidenceTargetSnapshot
            {
                BaseUrl = request.TargetBaseUrl,
                ServiceName = request.TargetServiceName,
                ServiceDigest = "target-digest",
                Capabilities = ["Query", "Extract"],
                SupportedQueryFormats = ["geojson", "json"],
                Layers =
                [
                    new MigrationEvidenceLayerSnapshot
                    {
                        LayerId = 0,
                        Name = "Parcels",
                        GeometryType = "Polygon",
                        SpatialReferenceWkid = 4326,
                        FeatureCount = 25,
                        HasAttachments = false,
                        Fields =
                        [
                            new MigrationEvidenceFieldSnapshot
                            {
                                Name = "objectid",
                                CanonicalName = "objectid",
                                Type = "Integer",
                                Nullable = false
                            }
                        ],
                        Extent = new MigrationEvidenceExtentSnapshot
                        {
                            MinX = -157.9,
                            MinY = 21.2,
                            MaxX = -157.7,
                            MaxY = 21.4,
                            SpatialReferenceWkid = 4326
                        },
                        LayerDigest = "target-layer-digest",
                        StyleDigest = "target-style-digest",
                        MapLibreStyleDigest = "maplibre-digest"
                    }
                ],
                OperationalSnapshot = new MigrationEvidenceOperationalSnapshot
                {
                    Status = "ready",
                    ReadyForCoordinatedDeploy = true,
                    Message = "Ready for coordinated deployment.",
                    MigrationPlanAvailable = true,
                    UpgradeRequired = false,
                    DatabaseCompatible = true
                }
            },
            Comparison = new MigrationEvidenceComparison
            {
                Capability =
                [
                    new MigrationComparisonCheck
                    {
                        CheckName = "service_capabilities",
                        Status = MigrationEvidenceStatus.Pass,
                        Scope = "service",
                        Summary = "Service-level capabilities matched."
                    }
                ],
                Style =
                [
                    new MigrationComparisonCheck
                    {
                        CheckName = "style_parity",
                        Status = MigrationEvidenceStatus.Pass,
                        Scope = "0->0",
                        Summary = "Canonical drawingInfo parity matched."
                    }
                ],
                Data =
                [
                    new MigrationComparisonCheck
                    {
                        CheckName = "core_query_parity",
                        Status = MigrationEvidenceStatus.Pass,
                        Scope = "0->0",
                        Summary = "Deterministic sample-row parity matched."
                    }
                ],
                OperationalReadiness =
                [
                    new MigrationComparisonCheck
                    {
                        CheckName = "deploy_preflight_ready",
                        Status = MigrationEvidenceStatus.Pass,
                        Scope = "instance",
                        Summary = "Deploy preflight reported the instance ready."
                    }
                ]
            },
            CutoverReadiness = new MigrationEvidenceReadinessSummary
            {
                State = MigrationReadinessState.PilotReady,
                Checklist =
                [
                    new CutoverChecklistItem
                    {
                        Name = "source_baseline_resolved",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Source baseline and mapped layers were resolved."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "target_mapping_resolved",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Target mappings were resolved."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "translation_inputs_resolved",
                        RequirementLevel = "production_required",
                        Status = MigrationEvidenceStatus.NotApplicable,
                        Summary = "No canonical style input was required for this scope."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "capability_parity",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Capability parity passed."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "style_parity",
                        RequirementLevel = "production_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Style parity passed."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "data_parity",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Data parity passed."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "geodesy_verified",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Geodesy verification passed."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "deploy_preflight_ready",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Deploy preflight reported the instance ready."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "migration_plan_clean",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Migration plan was available and clean."
                    },
                    new CutoverChecklistItem
                    {
                        Name = "rollback_reference_present",
                        RequirementLevel = "pilot_required",
                        Status = MigrationEvidenceStatus.Pass,
                        Summary = "Rollback reference was supplied."
                    }
                ]
            }
        });
    }

    private sealed class FakeMigrationEvidenceGenerator(
        Func<MigrationEvidenceRequest, CancellationToken, Task<MigrationEvidenceReport>> reportFactory) : IMigrationEvidenceGenerator
    {
        public TimeSpan Delay { get; set; }

        public async Task<MigrationEvidenceReport> GenerateAsync(
            MigrationEvidenceRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return await reportFactory(request, cancellationToken);
        }
    }

    private sealed class BlockingMigrationEvidenceReportStore : IMigrationEvidenceReportStore
    {
        private readonly TaskCompletionSource<bool> _storeAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private readonly List<MigrationEvidenceReport> _reports = [];

        public IReadOnlyList<MigrationEvidenceReport> StoredReports
        {
            get
            {
                lock (_sync)
                {
                    return _reports.ToArray();
                }
            }
        }

        public async Task StoreAsync(MigrationEvidenceReport report, CancellationToken cancellationToken = default)
        {
            _storeAttempted.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            lock (_sync)
            {
                _reports.Add(report);
            }
        }

        public Task<MigrationEvidenceReport?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(_reports.SingleOrDefault(report => report.ReportId == reportId));
            }
        }

        public Task<IReadOnlyList<MigrationEvidenceReportSummary>> ListAsync(
            int limit = 50,
            int offset = 0,
            MigrationEvidenceProvider? provider = null,
            MigrationCutoverProfile? cutoverProfile = null,
            MigrationReadinessState? readiness = null,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                IEnumerable<MigrationEvidenceReport> query = _reports;
                if (provider.HasValue)
                {
                    query = query.Where(report => report.Request.Provider == provider.Value);
                }

                if (cutoverProfile.HasValue)
                {
                    query = query.Where(report => report.Request.CutoverProfile == cutoverProfile.Value);
                }

                if (readiness.HasValue)
                {
                    query = query.Where(report => report.CutoverReadiness.State == readiness.Value);
                }

                var summaries = query
                    .OrderByDescending(report => report.GeneratedAt)
                    .Skip(Math.Max(offset, 0))
                    .Take(Math.Clamp(limit, 0, 200))
                    .Select(report => new MigrationEvidenceReportSummary
                    {
                        ReportId = report.ReportId,
                        SchemaVersion = report.SchemaVersion,
                        Provider = report.Request.Provider,
                        CutoverProfile = report.Request.CutoverProfile,
                        Readiness = report.CutoverReadiness.State,
                        SourceServiceUrl = report.Request.SourceServiceUrl,
                        TargetBaseUrl = report.Request.TargetBaseUrl,
                        TargetServiceName = report.Request.TargetServiceName,
                        ReportHash = report.ReportHash,
                        RequestedBy = report.Request.RequestedBy,
                        Summary = report.Request.Summary,
                        InventoryArtifactRef = report.Request.InventoryArtifactRef,
                        TranslationManifestRef = report.Request.TranslationManifestRef,
                        ImportJobId = report.Request.ImportJobId,
                        WarningCount = report.CutoverReadiness.Warnings.Length,
                        BlockerCount = report.CutoverReadiness.BlockingReasons.Length,
                        GeneratedAt = report.GeneratedAt
                    })
                    .ToArray();

                return Task.FromResult<IReadOnlyList<MigrationEvidenceReportSummary>>(summaries);
            }
        }

        public async Task WaitForStoreAttemptAsync(TimeSpan timeout) => _ = await _storeAttempted.Task.WaitAsync(timeout);
    }

    private sealed class BlockingMigrationEvidenceLifecycleObserver : IMigrationEvidenceLifecycleObserver
    {
        private readonly TaskCompletionSource<bool> _persistenceReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnReportPersistedAsync(string jobId, Guid reportId, CancellationToken cancellationToken)
        {
            _persistenceReached.TrySetResult(true);
            return _release.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForPersistenceAsync(TimeSpan timeout)
            => _ = await _persistenceReached.Task.WaitAsync(timeout);

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }
}
