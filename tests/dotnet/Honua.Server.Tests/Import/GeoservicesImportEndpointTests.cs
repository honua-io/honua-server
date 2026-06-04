// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
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
/// Integration tests for Geoservices service import endpoints
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public class GeoservicesImportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region POST /api/v1/admin/import/geoservices/discover

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithMissingServiceUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithInvalidUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new { ServiceUrl = "not-a-valid-url" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithHttpScheme_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "http://example.com/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithEmbeddedCredentials_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://user:pass@example.com/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("embedded credentials");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithCredentialQueryParameter_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer?token=plaintext-secret",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("credential query parameters");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithPlaintextToken_UsesCredentialWithoutPersistingJobState()
    {
        const string accessToken = "plaintext-discovery-token";
        var isolatedImportService = new TestGeoservicesImportService(TimeSpan.Zero);
        var isolatedFixture = new WebAppFixture()
            .ReplaceService<IGeoservicesImportService>(isolatedImportService);

        try
        {
            await isolatedFixture.InitializeAsync();

            var response = await isolatedFixture.Client.PostAsJsonAsync(
                "/api/v1/admin/import/geoservices/discover",
                new
                {
                    ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
                    TimeoutSeconds = 5,
                    Credentials = new
                    {
                        Mode = GeoservicesAuthenticationModes.Token,
                        AccessToken = accessToken
                    }
                });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain(accessToken);

            isolatedImportService.DiscoveryRequests.Should().ContainSingle();
            isolatedImportService.DiscoveryRequests.Single().Credentials.Should().NotBeNull();
            isolatedImportService.DiscoveryRequests.Single().Credentials!.AccessToken.Should().Be(accessToken);
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithUnresolvableHost_ReturnsBadRequest()
    {
        // Arrange - use a URL that cannot be resolved in DNS
        var request = new
        {
            ServiceUrl = "https://nonexistent.arcgis.server.invalid/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        // Assert - unresolved hosts should be rejected by SSRF hardening
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unresolvable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithPrivateNetworkUrl_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://127.0.0.1/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("private");
    }

    #endregion

    #region POST /api/v1/admin/import/geoservices/start

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithMissingServiceUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            LayerId = 0,
            TableName = "test_table"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithMissingTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("TableName is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithInvalidTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "invalid-table-name!" // Contains invalid characters
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid table name");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithHttpScheme_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "http://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_http_scheme"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithValidRequest_ReturnsAccepted()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_geoservices_import"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert - should return 202 Accepted with job info
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobId");
        content.Should().Contain("statusUrl");
        content.Should().Contain("cancelUrl");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithPlaintextToken_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_plaintext_token",
            Credentials = new
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessToken = "plaintext-token"
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("secret references");
        content.Should().NotContain("plaintext-token");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithCredentialQueryParameter_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer?token=plaintext-token",
            LayerId = 0,
            TableName = "test_query_token"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("credential query parameters");
        content.Should().NotContain("plaintext-token");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithSecretReferenceToken_DoesNotPersistPlaintextSecrets_AndWorkerReceivesResolvedValue()
    {
        const string envKey = "HONUA_TEST_ARCGIS_IMPORT_TOKEN";
        const string envValue = "resolved-arcgis-token-secret";
        var previousValue = Environment.GetEnvironmentVariable(envKey);
        var distributedCache = new TrackingDistributedCache();
        var isolatedImportService = new TestGeoservicesImportService(TimeSpan.FromMilliseconds(25));
        var isolatedFixture = new WebAppFixture()
            .ReplaceService<IGeoservicesImportService>(isolatedImportService)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(distributedCache);
                services.RemoveAll<IDistributedImportJobManager>();
                services.AddSingleton<IDistributedImportJobManager>(sp => new RedisImportJobManager(
                    sp.GetRequiredService<IUniversalProgressStore>(),
                    distributedCache,
                    sp.GetRequiredService<ILogger<RedisImportJobManager>>(),
                    new StaticHostEnvironment("Test")));
            });

        Environment.SetEnvironmentVariable(envKey, envValue);

        try
        {
            await isolatedFixture.InitializeAsync();

            var startResponse = await isolatedFixture.Client.PostAsJsonAsync(
                "/api/v1/admin/import/geoservices/start",
                new
                {
                    ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
                    LayerId = 0,
                    TableName = "test_geoservices_secret_token",
                    AutoPublish = false,
                    Credentials = new
                    {
                        Mode = GeoservicesAuthenticationModes.Token,
                        AccessTokenSecretReference = $"env:{envKey}"
                    }
                });

            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            using var startContent = await JsonDocument.ParseAsync(await startResponse.Content.ReadAsStreamAsync());
            var jobId = startContent.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();

            var requestPayload = Encoding.UTF8.GetString(distributedCache.Get($"geoservices:import:request:{jobId}")!);
            requestPayload.Should().Contain($"\"accessTokenSecretReference\":\"env:{envKey}\"");
            requestPayload.Should().NotContain(envValue);
            requestPayload.Should().NotContain("\"accessToken\":");

            using var completed = await WaitForJobStatusAsync(
                isolatedFixture.Client,
                jobId!,
                GeoservicesImportStatus.Completed,
                TimeSpan.FromSeconds(20));
            completed.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
            JobStatusMatches(completed.RootElement.GetProperty("status"), GeoservicesImportStatus.Completed)
                .Should().BeTrue();

            isolatedImportService.ImportRequests.Should().ContainSingle();
            var importRequest = isolatedImportService.ImportRequests.Single();
            importRequest.Credentials.Should().NotBeNull();
            importRequest.Credentials!.AccessToken.Should().Be(envValue);
            importRequest.Credentials.AccessTokenSecretReference.Should().Be($"env:{envKey}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previousValue);
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithPrivateNetworkUrl_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://127.0.0.1/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_private_url"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("private");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WhenDistributedCoordinationIsUnavailable_ReturnsServiceUnavailable()
    {
        await using var degradedFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDistributedImportJobManager, DegradedImportJobManager>();
            });

        await degradedFixture.InitializeAsync();

        var response = await degradedFixture.Client.PostAsJsonAsync(
            "/api/v1/admin/import/geoservices/start",
            new
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
                LayerId = 0,
                TableName = "test_degraded_import"
            });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("coordination is unavailable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
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
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(distributedCache);
                services.RemoveAll<IDistributedImportJobManager>();
                services.AddSingleton<IDistributedImportJobManager>(sp => new RedisImportJobManager(
                    sp.GetRequiredService<IUniversalProgressStore>(),
                    distributedCache,
                    sp.GetRequiredService<ILogger<RedisImportJobManager>>(),
                    new StaticHostEnvironment("Production"),
                    redis));
            });

        try
        {
            await isolatedFixture.InitializeAsync();

            var response = await isolatedFixture.Client.PostAsJsonAsync(
                "/api/v1/admin/import/geoservices/start",
                new
                {
                    ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
                    LayerId = 0,
                    TableName = "test_geoservices_queue_rollback"
                });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Contain("Distributed import queue is temporarily unavailable");

            distributedCache.Keys.Should().NotContain(key => key.StartsWith("geoservices:import:request:", StringComparison.Ordinal));
            distributedCache.Keys.Should().NotContain(key => key.StartsWith("universal:progress:", StringComparison.Ordinal));
            distributedCache.Keys.Should().NotContain(key => key.StartsWith("universal:type:", StringComparison.Ordinal));
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithUnresolvableHost_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://nonexistent.arcgis.server.invalid/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_unresolvable_url"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unresolvable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task BackgroundWorker_WithUnsafeStoredServiceUrl_FailsJobBeforeImport()
    {
        using var scope = _fixture.Services.CreateScope();
        var jobManager = scope.ServiceProvider.GetRequiredService<IDistributedImportJobManager>();

        var jobId = Guid.NewGuid().ToString("N")[..12];
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://127.0.0.1/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "unsafe_job_url",
            TargetSrid = 4326,
            OverwriteExisting = false,
            RequestTimeoutSeconds = 30,
            MaxRetries = 0,
            AutoPublish = false
        };

        var progress = GeoservicesImportProgress.CreateInitial(
            jobId,
            request.ServiceUrl,
            request.LayerId,
            request.TableName);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        GeoservicesImportProgress? latestProgress = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            latestProgress = await jobManager.ProgressStore.GetProgressAsync(jobId);
            if (latestProgress is { Status: GeoservicesImportStatus.Failed })
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        latestProgress.Should().NotBeNull();
        latestProgress!.Status.Should().Be(GeoservicesImportStatus.Failed);
        latestProgress.ErrorMessage.Should().Contain("not allowed");
    }

    #endregion

    #region GET /api/v1/admin/import/geoservices/jobs/{jobId}

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task GetJobStatus_WithNonExistentJob_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/geoservices/jobs/nonexistent123");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task GetJobStatus_AfterStartingJob_ReturnsProgress()
    {
        // Arrange - start a job first
        var startRequest = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_job_status",
            AutoPublish = true,
            ServiceName = "imported-service"
        };

        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", startRequest);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startContent = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startContent.GetProperty("jobId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/v1/admin/import/geoservices/jobs/{jobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobId");
        content.Should().Contain("status");
        content.Should().Contain("tableName");
        content.Should().Contain("sourceKind");
        content.Should().Contain("sourceUrl");
        content.Should().Contain("imported-service");
    }

    #endregion

    #region POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithNonExistentJob_ReturnsNotFound()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/geoservices/jobs/nonexistent123/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel")]
    public async Task CancelJob_AfterStartingJob_ReturnsSuccess()
    {
        // Arrange - start a job first
        var startRequest = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_cancel_job"
        };

        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", startRequest);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startContent = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startContent.GetProperty("jobId").GetString();

        // Act
        var response = await _client.PostAsync($"/api/v1/admin/import/geoservices/jobs/{jobId}/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(jobId!);
        content.Should().Contain("cancellation");
    }

    #endregion

    #region GET /api/v1/admin/import/geoservices/jobs

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs")]
    public async Task ListJobs_ReturnsJobsList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/geoservices/jobs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobs");
    }

    #endregion

    private static async Task<JsonDocument> WaitForJobStatusAsync(
        HttpClient client,
        string jobId,
        GeoservicesImportStatus expectedStatus,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/v1/admin/import/geoservices/jobs/{jobId}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                var status = document.RootElement.GetProperty("status");
                if (JobStatusMatches(status, expectedStatus))
                {
                    return document;
                }

                document.Dispose();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Job {jobId} did not reach {expectedStatus} within {timeout}.");
    }

    private static bool JobStatusMatches(JsonElement status, GeoservicesImportStatus expectedStatus)
        => status.ValueKind switch
        {
            JsonValueKind.String => string.Equals(status.GetString(), expectedStatus.ToString(), StringComparison.Ordinal),
            JsonValueKind.Number => status.TryGetInt32(out var numericStatus) && numericStatus == (int)expectedStatus,
            _ => false
        };
}

internal sealed class TestGeoservicesImportService(TimeSpan delay) : IGeoservicesImportService
{
    public ConcurrentQueue<GeoservicesDiscoveryRequest> DiscoveryRequests { get; } = new();
    public ConcurrentQueue<GeoservicesImportRequest> ImportRequests { get; } = new();

    public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        DiscoveryRequests.Enqueue(request);
        return Task.FromResult(new GeoservicesServiceInfo
        {
            ServiceUrl = request.ServiceUrl,
            ServiceName = "Test",
            Layers =
            [
                new GeoservicesLayerInfo
                {
                    Id = 0,
                    Name = "Test Layer"
                }
            ]
        });
    }

    public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new MigrationSourceInventoryArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Test",
                BaseUrl = request.ServiceUrl,
                Product = "ArcGIS GeoServices REST"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = request.Credentials?.GetNormalizedMode() ?? GeoservicesAuthenticationModes.Anonymous,
                CredentialsSupplied = request.Credentials?.HasCredentialMaterial == true,
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness
            {
                Status = "complete"
            },
            Summary = new MigrationInventorySummary(),
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Test scan artifact."
            }
        });

    public Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        CancellationToken cancellationToken = default)
        => ImportLayerAsync(request, progress: null, cancellationToken);

    public async Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        IProgress<GeoservicesImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ImportRequests.Enqueue(request);

        var current = GeoservicesImportProgress.CreateInitial(
            request.JobId ?? Guid.NewGuid().ToString("N"),
            request.ServiceUrl,
            request.LayerId,
            request.TableName,
            sourceLayerName: "Test Layer",
            estimatedTotalFeatures: 1);
        progress?.Report(current);

        await Task.Delay(delay, cancellationToken);

        progress?.Report(current with
        {
            Status = GeoservicesImportStatus.Completed,
            FeaturesProcessed = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Import completed"
        });

        return GeoservicesImportResult.CreateSuccess(
            request.TableName,
            request.ServiceUrl,
            request.LayerId,
            featureCount: 1,
            sourceLayerName: "Test Layer",
            serviceName: request.ServiceName);
    }

    public Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
        MigrationManifestArtifact manifest,
        IReadOnlyDictionary<string, int> publishedLayerMap,
        Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<MigrationRelationshipApplyOutcome>());
}

internal sealed class DegradedImportJobManager : IDistributedImportJobManager, IImportCoordinationHealth
{
    public IDistributedJobQueueService JobQueue { get; } = new NoopDistributedJobQueue();
    public IDistributedLeaderElection LeaderElection { get; } = new NoopDistributedLeaderElection();
    public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore { get; } = new NoopProgressStore<GeoservicesImportProgress>();
    public IDistributedProgressStore<GeoservicesImportRequest> RequestStore { get; } = new NoopProgressStore<GeoservicesImportRequest>();
    public bool CanAcceptNewJobs => false;
}

internal sealed class NoopDistributedJobQueue : IDistributedJobQueueService
{
    public Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task CompleteAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecoverInFlightAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
}

internal sealed class NoopDistributedLeaderElection : IDistributedLeaderElection
{
    public bool IsLeader => false;
    public string InstanceId => "noop";
    public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NoopProgressStore<T> : IDistributedProgressStore<T> where T : class
{
    public Task SetProgressAsync(string jobId, T progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<T?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<T?>(null);
    public Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

internal sealed class TrackingDistributedCache : IDistributedCache
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

internal sealed class StaticHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = nameof(GeoservicesImportEndpointTests);
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
