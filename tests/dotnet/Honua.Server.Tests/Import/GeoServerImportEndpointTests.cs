// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for GeoServer import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public class GeoServerImportEndpointTests : IAsyncLifetime
{
    private readonly TestGeoServerImportService _importService = new(TimeSpan.FromMilliseconds(250));
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public GeoServerImportEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IGeoServerImportService>(_importService);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_WithMissingGeoServerUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GeoServerRestUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_WithInvalidUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new
        {
            GeoServerRestUrl = "not-a-valid-url"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(GeoServerServiceUrlValidation.InvalidHttpsUrlMessage);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_WithValidUrl_ReturnsServiceInfo()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var content = await response.Content.ReadFromJsonAsync<JsonDocument>();
        content.Should().NotBeNull();
        content!.RootElement.GetProperty("geoServerRestUrl").GetString().Should().Be("https://example.com/geoserver/rest");
        content.RootElement.GetProperty("version").GetString().Should().Be("2.28.0");
        content.RootElement.GetProperty("workspaces").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithMissingGeoServerUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            DryRun = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GeoServerRestUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithoutDryRunAndWithoutApplyMode_ReturnsSafetyGateError()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("applyMode=true");
        _importService.ImportRequests.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithoutDryRun_QueuesApplyPlanJob()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = false,
            ApplyMode = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        using var completed = await WaitForJobStatusAsync(jobId, "Completed", TimeSpan.FromSeconds(20));
        completed.RootElement.GetProperty("progress").GetProperty("currentPhase").GetString().Should().Be("Apply plan generated");
        completed.RootElement.GetProperty("progress").GetProperty("applyPlan").GetProperty("replayToken").GetString()
            .Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        _importService.ImportRequests.Should().ContainSingle()
            .Which.DryRun.Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithPlaintextPassword_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            Username = "admin",
            Password = "plaintext-secret",
            DryRun = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("passwordSecretReference");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithSecretReferencePassword_DoesNotPersistPlaintextSecrets_AndWorkerReceivesResolvedValue()
    {
        const string envKey = "HONUA_TEST_GEOSERVER_IMPORT_PASSWORD";
        const string envValue = "resolved-geoserver-secret";
        var previousValue = Environment.GetEnvironmentVariable(envKey);
        var distributedCache = new TrackingDistributedCache();
        var isolatedImportService = new TestGeoServerImportService(TimeSpan.FromMilliseconds(25));
        var isolatedFixture = new WebAppFixture()
            .ReplaceService<IGeoServerImportService>(isolatedImportService)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(distributedCache);
                services.RemoveAll<GeoServerImportJobManager>();
                services.AddSingleton(sp => new GeoServerImportJobManager(
                    sp.GetRequiredService<IUniversalProgressStore>(),
                    distributedCache,
                    sp.GetRequiredService<ILogger<GeoServerImportJobManager>>(),
                    new StaticHostEnvironment("Test")));
            });

        Environment.SetEnvironmentVariable(envKey, envValue);

        try
        {
            await isolatedFixture.InitializeAsync();

            var startResponse = await isolatedFixture.Client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
            {
                GeoServerRestUrl = "https://example.com/geoserver/rest",
                Username = "admin",
                PasswordSecretReference = $"env:{envKey}",
                DryRun = false,
                ApplyMode = true
            });

            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var jobId = await GetJobIdAsync(startResponse);

            var requestPayload = Encoding.UTF8.GetString(distributedCache.Get($"geoserver:import:request:{jobId}")!);
            requestPayload.Should().Contain($"\"passwordSecretReference\":\"env:{envKey}\"");
            requestPayload.Should().NotContain(envValue);
            requestPayload.Should().NotContain("\"password\":");

            using var completed = await WaitForJobStatusAsync(isolatedFixture.Client, jobId, "Completed", TimeSpan.FromSeconds(20));
            completed.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
            completed.RootElement.GetProperty("status").GetString().Should().Be("Completed");
            var completedPayload = completed.RootElement.GetRawText();
            completedPayload.Should().Contain("\"applyPlan\"");
            completedPayload.Should().NotContain(envValue);
            completedPayload.Should().NotContain("passwordSecretReference");

            isolatedImportService.ImportRequests.Should().ContainSingle();
            isolatedImportService.ImportRequests.Single().Password.Should().Be(envValue);
            isolatedImportService.ImportRequests.Single().PasswordSecretReference.Should().Be($"env:{envKey}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previousValue);
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithValidDryRun_QueuesAndCompletesJob()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        var listResponse = await _client.GetAsync("/api/v1/admin/import/geoserver/jobs");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listResponse.Content.ReadAsStringAsync()).Should().Contain(jobId);

        var completed = await WaitForJobStatusAsync(jobId, "Completed", TimeSpan.FromSeconds(20));
        completed.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
        completed.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        completed.RootElement.GetProperty("progress").GetProperty("currentPhase").GetString().Should().Be("Dry run completed");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WhenQueueBecomesUnavailable_RollsBackPersistedJobState()
    {
        var distributedCache = new TrackingDistributedCache();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        RedisImportTestStubs.ConfigureDurableProgressTransactions(database);
        database.ListLeftPushAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromException<long>(new RedisConnectionException(ConnectionFailureType.UnableToResolvePhysicalConnection, "queue unavailable")));
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>())
            .Returns(database);

        var isolatedFixture = new WebAppFixture()
            .ReplaceService<IGeoServerImportService>(_importService)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(distributedCache);
                services.RemoveAll<GeoServerImportJobManager>();
                services.AddSingleton(sp => new GeoServerImportJobManager(
                    sp.GetRequiredService<IUniversalProgressStore>(),
                    distributedCache,
                    sp.GetRequiredService<ILogger<GeoServerImportJobManager>>(),
                    new StaticHostEnvironment("Production"),
                    redis));
            });

        try
        {
            await isolatedFixture.InitializeAsync();

            var response = await isolatedFixture.Client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
            {
                GeoServerRestUrl = "https://example.com/geoserver/rest",
                DryRun = true
            });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Contain("Distributed GeoServer import queue is temporarily unavailable");

            distributedCache.Keys.Should().NotContain(key => key.StartsWith("geoserver:import:request:", StringComparison.Ordinal));
            distributedCache.Keys.Should().NotContain(key => key.StartsWith("universal:progress:", StringComparison.Ordinal));
            distributedCache.Keys.Should().NotContain(key => key.StartsWith("universal:type:", StringComparison.Ordinal));
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoserver/jobs")]
    public async Task ListJobs_WithQueuedJob_ReturnsActiveJobs()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        var response = await _client.GetAsync("/api/v1/admin/import/geoserver/jobs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();
        payload!.RootElement.GetProperty("jobs").EnumerateArray()
            .Select(element => element.GetProperty("jobId").GetString())
            .Should().Contain(jobId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoserver/jobs/{jobId}")]
    public async Task GetJobStatus_WithNonExistentJob_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/geoserver/jobs/nonexistent123");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GeoServer import job not found");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithQueuedJob_ReturnsCancelled()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        var cancelResponse = await _client.PostAsync($"/api/v1/admin/import/geoserver/jobs/{jobId}/cancel", null);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var cancelled = await WaitForJobStatusAsync(jobId, "Cancelled", TimeSpan.FromSeconds(10));
        cancelled.RootElement.GetProperty("status").GetString().Should().Be("Cancelled");
    }

    private async Task<string> GetJobIdAsync(HttpResponseMessage response)
    {
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return document!.RootElement.GetProperty("jobId").GetString()!;
    }

    private Task<JsonDocument> WaitForJobStatusAsync(string jobId, string expectedStatus, TimeSpan timeout)
        => WaitForJobStatusAsync(_client, jobId, expectedStatus, timeout);

    private static async Task<JsonDocument> WaitForJobStatusAsync(HttpClient client, string jobId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/v1/admin/import/geoserver/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
            var status = payload!.RootElement.GetProperty("status").GetString();
            if (status == expectedStatus)
            {
                return payload;
            }

            payload.Dispose();
            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for GeoServer import job '{jobId}' to reach status '{expectedStatus}'.");
    }

    private sealed class TestGeoServerImportService(TimeSpan delay) : IGeoServerImportService
    {
        public ConcurrentQueue<GeoServerImportRequest> ImportRequests { get; } = new();

        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GeoServerServiceInfo
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Version = "2.28.0",
                Workspaces =
                [
                    new GeoServerWorkspaceInfo
                    {
                        Name = "demo"
                    }
                ],
                DataStores =
                [
                    new GeoServerDataStoreInfo
                    {
                        Name = "states",
                        WorkspaceName = "demo",
                        Type = "PostGIS"
                    }
                ],
                Layers =
                [
                    new GeoServerLayerInfo
                    {
                        Name = "states",
                        WorkspaceName = "demo"
                    }
                ]
            });
        }

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MigrationSourceInventoryArtifact
            {
                SourceKind = "geoserver-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = "Demo GeoServer",
                    BaseUrl = request.GeoServerRestUrl,
                    Product = "GeoServer",
                    Version = "2.28.0",
                    ServiceType = "REST"
                },
                AuthPosture = new MigrationInventoryAuthPosture
                {
                    Mode = "anonymous",
                    AccessConfirmed = true
                },
                ScanCompleteness = new MigrationInventoryCompleteness
                {
                    Status = "complete"
                },
                Summary = new MigrationInventorySummary
                {
                    ContainerCount = 1,
                    ResourceCount = 1,
                    StyleCount = 0,
                    ExternalDependencyCount = 1,
                    CompatibleCount = 2,
                    PartiallyCompatibleCount = 0,
                    IncompatibleCount = 0
                },
                OverallCompatibility = new MigrationCompatibilityAssessment
                {
                    Level = "compatible",
                    Reason = "Test scan artifact."
                },
                Containers =
                [
                    new MigrationInventoryContainer
                    {
                        Id = "workspace:demo",
                        Kind = "workspace",
                        Name = "demo",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Workspace can be migrated."
                        }
                    }
                ],
                Resources =
                [
                    new MigrationInventoryResource
                    {
                        Id = "layer:demo:states",
                        ContainerId = "workspace:demo",
                        Kind = "layer",
                        Name = "states",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Layer can be migrated."
                        }
                    }
                ],
                ExternalDependencies =
                [
                    new MigrationExternalDependency
                    {
                        Id = "datastore:demo:states",
                        ContainerId = "workspace:demo",
                        Kind = "datastore",
                        Name = "states",
                        DependencyType = "PostGIS",
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "compatible",
                            Reason = "Dependency can be migrated."
                        }
                    }
                ]
            });
        }

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportConfigurationAsync(request, progress: null, cancellationToken);

        public async Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            IProgress<GeoServerImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            ImportRequests.Enqueue(request);
            var applyPlan = request.DryRun
                ? null
                : MigrationApplyPlanBuilder.Build(MigrationManifestTranslator.Translate(
                    await ScanSourceAsync(new GeoServerDiscoveryRequest
                    {
                        GeoServerRestUrl = request.GeoServerRestUrl
                    }, cancellationToken)));
            var resourceCount = applyPlan?.Summary.TotalStepCount ?? 3;

            var current = GeoServerImportProgress.CreateInitial(
                request.JobId ?? Guid.NewGuid().ToString("N"),
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                estimatedTotalResources: resourceCount,
                sourceGeoServerVersion: "2.28.0");
            progress?.Report(current);

            current = current with
            {
                Status = GeoServerImportStatus.Discovering,
                CurrentPhase = "Discovering GeoServer configuration",
                SourceGeoServerVersion = "2.28.0"
            };
            progress?.Report(current);

            await Task.Delay(delay, cancellationToken);

            current = current with
            {
                Status = GeoServerImportStatus.Completed,
                ResourcesProcessed = resourceCount,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = request.DryRun ? "Dry run completed" : "Apply plan generated",
                ApplyPlan = applyPlan
            };
            progress?.Report(current);

            return GeoServerImportResult.CreateSuccess(
                    request.GeoServerRestUrl,
                    request.TargetHonuaUrl,
                    workspacesImported: request.DryRun ? 1 : 0,
                    dataStoresImported: request.DryRun ? 1 : 0,
                    layersImported: request.DryRun ? 1 : 0,
                    sourceGeoServerVersion: "2.28.0",
                    wasDryRun: request.DryRun)
                with
            {
                FailedResources = 0,
                ResourcesPlanned = applyPlan?.Summary.TotalStepCount ?? 0,
                ApplyPlan = applyPlan
            };
        }
    }

    private sealed class TrackingDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public IReadOnlyCollection<string> Keys
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Keys.ToArray();
                }
            }
        }

        public byte[]? Get(string key)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(key, out var value) ? value.ToArray() : null;
            }
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromResult(Get(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
            => Task.CompletedTask;

        public void Remove(string key)
        {
            lock (_gate)
            {
                _entries.Remove(key);
            }
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            lock (_gate)
            {
                _entries[key] = value.ToArray();
            }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(GeoServerImportEndpointTests);
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
