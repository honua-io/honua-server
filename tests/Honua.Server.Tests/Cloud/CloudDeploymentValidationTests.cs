// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Cloud;

/// <summary>
/// Validates real cloud deployments after infrastructure provisioning completes.
/// These tests are intended to run against a live environment after Terraform apply.
/// </summary>
[Protocol(Protocols.Admin)]
public sealed class CloudDeploymentValidationTests
{
    private const string BaseUrlEnv = "HONUA_CLOUD_TEST_BASE_URL";
    private const string AdminApiKeyEnv = "HONUA_CLOUD_TEST_ADMIN_API_KEY";
    private const string ExpectedEnvironmentEnv = "HONUA_CLOUD_TEST_EXPECTED_ENVIRONMENT";
    private const string ExpectedDeploymentModeEnv = "HONUA_CLOUD_TEST_EXPECTED_DEPLOYMENT_MODE";
    private const string ExpectedCoordinatedDeployReadyEnv = "HONUA_CLOUD_TEST_EXPECT_READY_FOR_COORDINATED_DEPLOY";
    private const string PlatformEnv = "HONUA_CLOUD_TEST_PLATFORM";
    private const string DeployTargetIdEnv = "HONUA_CLOUD_TEST_DEPLOY_TARGET_ID";
    private const string ExpectDeployPlanSupportEnv = "HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT";
    private const string ExpectMutationSupportEnv = "HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT";
    private const string DeployDesiredRevisionEnv = "HONUA_CLOUD_TEST_DEPLOY_DESIRED_REVISION";
    private const string DeployCurrentRevisionEnv = "HONUA_CLOUD_TEST_DEPLOY_CURRENT_REVISION";
    private const string ExecuteDeployOperationEnv = "HONUA_CLOUD_TEST_EXECUTE_DEPLOY_OPERATION";
    private const string VerifyDeployRollbackEnv = "HONUA_CLOUD_TEST_VERIFY_DEPLOY_ROLLBACK";
    private const string DeployTimeoutSecondsEnv = "HONUA_CLOUD_TEST_DEPLOY_TIMEOUT_SECONDS";
    private const string ImportTablePrefixEnv = "HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX";
    private const string ImportTimeoutSecondsEnv = "HONUA_CLOUD_TEST_IMPORT_TIMEOUT_SECONDS";
    private const string PublishDbHostEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_HOST";
    private const string PublishDbPortEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_PORT";
    private const string PublishDbNameEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_NAME";
    private const string PublishDbUsernameEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_USERNAME";
    private const string PublishDbPasswordEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_PASSWORD";
    private const string PublishDbSslModeEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_SSL_MODE";
    private const string PublishDbSslRequiredEnv = "HONUA_CLOUD_TEST_PUBLISH_DB_SSL_REQUIRED";
    private const string ApiKeyHeader = "X-API-Key";
    private const string ImportedGeoJsonGeometryType = "Point";

    [CloudTest(BaseUrlEnv)]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /healthz/live")]
    [Endpoint("GET /healthz/ready")]
    [Endpoint("GET /openapi.json")]
    [Endpoint("GET /rest/services")]
    public async Task PublicDeploymentEndpoints_AreHealthy()
    {
        using var client = CreateClient();
        var checks = new[]
        {
            "/healthz/live",
            "/healthz/ready",
            "/openapi.json",
            "/rest/services?f=json"
        };

        foreach (var path in checks)
        {
            using var request = CreateOptionalApiKeyRequest(HttpMethod.Get, path);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"'{path}' should respond successfully from the deployed environment");
        }
    }

    [CloudTest(BaseUrlEnv, AdminApiKeyEnv)]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task DeployPreflight_ReflectsExpectedEnvironmentState()
    {
        using var client = CreateClient();
        using var request = CreateAdminRequest(HttpMethod.Get, "/api/v1/admin/deploy/preflight");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("deploymentMode").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("readiness").GetProperty("isReady").GetBoolean().Should().BeTrue();
        root.GetProperty("migration").GetProperty("planAvailable").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        var expectedEnvironment = Environment.GetEnvironmentVariable(ExpectedEnvironmentEnv);
        if (!string.IsNullOrWhiteSpace(expectedEnvironment))
        {
            root.GetProperty("environment").GetString().Should().Be(expectedEnvironment);
        }

        var expectedDeploymentMode = Environment.GetEnvironmentVariable(ExpectedDeploymentModeEnv);
        if (!string.IsNullOrWhiteSpace(expectedDeploymentMode))
        {
            root.GetProperty("deploymentMode").GetString().Should().Be(expectedDeploymentMode);
        }
        else
        {
            var platform = Environment.GetEnvironmentVariable(PlatformEnv);
            if (string.Equals(platform, "aws-lambda", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(platform, "azure-functions", StringComparison.OrdinalIgnoreCase))
            {
                root.GetProperty("deploymentMode").GetString().Should().Be("SingleInstance");
            }
        }

        var expectedReady = Environment.GetEnvironmentVariable(ExpectedCoordinatedDeployReadyEnv);
        if (bool.TryParse(expectedReady, out var expectedReadyValue))
        {
            root.GetProperty("readyForCoordinatedDeploy").GetBoolean().Should().Be(expectedReadyValue);
        }
    }

    [CloudTest(BaseUrlEnv, AdminApiKeyEnv)]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/deploy/plan")]
    public async Task DeployPlanEndpoint_ReturnsPlan_WhenTargetConfigured_OrNotFoundContract_WhenNoTargetConfigured()
    {
        using var client = CreateClient();
        var expectsDeployPlanSupport = GetOptionalBool(ExpectDeployPlanSupportEnv, defaultValue: true);

        var configuredTargetId = Environment.GetEnvironmentVariable(DeployTargetIdEnv);
        var targetId = string.IsNullOrWhiteSpace(configuredTargetId)
            ? $"cloud-validation-missing-{Guid.NewGuid():N}"
            : configuredTargetId;
        var currentRevision = GetOptionalEnv(DeployCurrentRevisionEnv);
        var desiredRevision = ResolveDesiredRevision(currentRevision);

        using var request = CreateAdminJsonRequest(HttpMethod.Post, "/api/v1/admin/deploy/plan", new
        {
            targetId,
            desiredRevision,
            currentRevision
        });

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        if (!expectsDeployPlanSupport)
        {
            AssertUnsupportedOperationContract(response, payload);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuredTargetId))
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"response: {payload}");
            payload.Should().Contain("Deploy target", "unknown deploy targets should return the admin problem contract");
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var target = root.GetProperty("target");

        target.GetProperty("targetId").GetString().Should().Be(targetId);
        target.GetProperty("desiredRevision").GetString().Should().Be(desiredRevision);
        root.GetProperty("readyToSubmit").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        root.GetProperty("backendRegistered").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        root.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("blockingReasons").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [CloudTest(BaseUrlEnv, AdminApiKeyEnv)]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    [Endpoint("GET /api/v1/admin/openapi.json")]
    public async Task AdminControlPlaneEndpoints_AreAvailableInCloudDeployment()
    {
        using var client = CreateClient();

        using (var migrationsRequest = CreateAdminRequest(HttpMethod.Get, "/api/v1/admin/observability/migrations"))
        using (var migrationsResponse = await client.SendAsync(migrationsRequest))
        {
            migrationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await migrationsResponse.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("planAvailable").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        }

        using (var openApiRequest = CreateAdminRequest(HttpMethod.Get, "/api/v1/admin/openapi.json"))
        using (var openApiResponse = await client.SendAsync(openApiRequest))
        {
            openApiResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync());
            var paths = document.RootElement.GetProperty("paths");
            paths.EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .Contain(path => path.Contains("/deploy/", StringComparison.Ordinal),
                    "the deployed admin contract should expose control-plane deploy endpoints");
        }
    }

    [CloudTest(BaseUrlEnv, AdminApiKeyEnv, DeployTargetIdEnv, DeployDesiredRevisionEnv)]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/deploy/operations")]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task DeployOperation_CanPromoteAndOptionallyRollback_WhenLiveExecutionIsEnabled()
    {
        if (!GetOptionalBool(ExecuteDeployOperationEnv))
        {
            return;
        }

        using var client = CreateClient();
        var targetId = GetRequiredEnv(DeployTargetIdEnv);
        var currentRevision = GetOptionalEnv(DeployCurrentRevisionEnv);
        var desiredRevision = ResolveDesiredRevision(currentRevision);
        var timeout = TimeSpan.FromSeconds(GetDeployTimeoutSeconds());

        var promoteOperation = await CreateDeployOperationAsync(client, targetId, desiredRevision, currentRevision);
        var promoted = await WaitForDeployOperationTerminalStateAsync(client, promoteOperation.OperationId, timeout);
        promoted.GetProperty("status").GetString().Should().Be("Succeeded");
        promoted.GetProperty("target").GetProperty("desiredRevision").GetString().Should().Be(desiredRevision);

        if (!GetOptionalBool(VerifyDeployRollbackEnv))
        {
            return;
        }

        var rollbackOperation = await RequestRollbackAsync(client, promoteOperation.OperationId);
        rollbackOperation.GetProperty("status").GetString().Should().Be("RollbackRequested");

        var rolledBack = await WaitForDeployOperationTerminalStateAsync(client, promoteOperation.OperationId, timeout);
        rolledBack.GetProperty("status").GetString().Should().Be("RolledBack");

        var restoreOperation = await CreateDeployOperationAsync(client, targetId, desiredRevision, currentRevision);
        var restored = await WaitForDeployOperationTerminalStateAsync(client, restoreOperation.OperationId, timeout);
        restored.GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [CloudTest(BaseUrlEnv, AdminApiKeyEnv, ImportTablePrefixEnv)]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/admin/import/uploads/{uploadId}/progress")]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    [Endpoint("POST /api/v1/admin/connections")]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task CloudStagedImport_CompletesAndPublishedLayerBecomesPublicMetadata_WhenMutationChecksAreEnabled()
    {
        if (!GetOptionalBool(ExpectMutationSupportEnv, defaultValue: true))
        {
            return;
        }

        using var client = CreateClient();
        var tablePrefix = GetRequiredEnv(ImportTablePrefixEnv);
        var tableName = $"{tablePrefix}_{Guid.NewGuid():N}".ToLowerInvariant();

        using var request = CreateAdminRequest(HttpMethod.Post, "/api/v1/admin/import/upload");
        request.Content = CreateImportContent(tableName);

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var uploadId = responseDocument.RootElement.GetProperty("uploadId").GetString();
        var jobId = responseDocument.RootElement.GetProperty("jobId").GetString();
        uploadId.Should().NotBeNullOrWhiteSpace();
        jobId.Should().NotBeNullOrWhiteSpace();

        var timeout = TimeSpan.FromSeconds(GetImportTimeoutSeconds());
        JsonElement? uploadProgress = await TryWaitForUploadCompletionAsync(
            client,
            uploadId!,
            TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, 60)));

        var importProgress = await WaitForImportJobProgressAsync(client, jobId!, timeout);
        ReadImportStatus(importProgress.GetProperty("status")).Should().Be(ImportStatus.Completed);
        importProgress.GetProperty("tableName").GetString().Should().Be(tableName);
        importProgress.GetProperty("featuresProcessed").GetInt32().Should().Be(1);
        importProgress.GetProperty("failedFeatures").GetInt32().Should().Be(0);

        uploadProgress ??= await TryGetUploadProgressAsync(client, uploadId!);
        if (uploadProgress is { } completedUploadProgress
            && completedUploadProgress.TryGetProperty("cloudFileId", out var cloudFileIdElement)
            && cloudFileIdElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            cloudFileIdElement.GetString().Should().NotBeNullOrWhiteSpace();
        }

        var connectionId = await CreateSecureConnectionAsync(client, tableName);
        var importedTable = await DiscoverImportedTableAsync(client, connectionId, tableName, timeout);
        var publishedLayer = await PublishImportedTableAsync(client, connectionId, importedTable, tableName);
        var serviceLayer = await WaitForPublishedServiceLayerAsync(
            client,
            publishedLayer.ServiceName,
            publishedLayer.LayerId,
            timeout);
        var publicLayer = await WaitForPublishedLayerMetadataAsync(
            client,
            publishedLayer.ServiceName,
            publishedLayer.LayerId,
            timeout);

        var expectedLayerName = $"Cloud Validation {tableName}";
        serviceLayer.GetProperty("id").GetInt32().Should().Be(publishedLayer.LayerId);
        serviceLayer.GetProperty("name").GetString().Should().Be(expectedLayerName);

        publicLayer.GetProperty("id").GetInt32().Should().Be(publishedLayer.LayerId);
        publicLayer.GetProperty("name").GetString().Should().Be(expectedLayerName);
        publicLayer.GetProperty("type").GetString().Should().Be("Feature Layer");
        publicLayer.GetProperty("geometryType").GetString().Should().NotBeNullOrWhiteSpace();
        publicLayer.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .OfType<string>()
            .Should()
            .Contain(fieldName => string.Equals(fieldName, importedTable.PrimaryKey, StringComparison.OrdinalIgnoreCase),
                "the published layer metadata should expose the imported table primary key");
    }

    private static HttpClient CreateClient()
    {
        var baseUrl = GetRequiredEnv(BaseUrlEnv).TrimEnd('/');
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static HttpRequestMessage CreateAdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ApiKeyHeader, GetRequiredEnv(AdminApiKeyEnv));
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static HttpRequestMessage CreateOptionalApiKeyRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (TryGetEnv(AdminApiKeyEnv, out var apiKey))
        {
            request.Headers.Add(ApiKeyHeader, apiKey);
        }

        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static HttpRequestMessage CreateAdminJsonRequest<T>(HttpMethod method, string path, T payload)
    {
        var request = CreateAdminRequest(method, path);
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private static void AssertUnsupportedOperationContract(HttpResponseMessage response, string payload)
    {
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, $"response: {payload}");

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        root.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.MethodNotAllowed);
        root.GetProperty("title").GetString().Should().Be("Method Not Allowed");
        root.GetProperty("detail").GetString().Should().ContainEquivalentOf("not supported");
    }

    private static MultipartFormDataContent CreateImportContent(string tableName)
    {
        const string geoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "geometry": {
                "type": "Point",
                "coordinates": [-157.8583, 21.3069]
              },
              "properties": {
                "name": "CloudValidation"
              }
            }
          ]
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJson, Encoding.UTF8, "application/geo+json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "cloud-validation.geojson"
        };

        content.Add(fileContent);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent("4326"), "SourceSrid");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");
        content.Add(new StringContent("true"), "ForceBackground");
        content.Add(new StringContent("true"), "TrackProgress");
        return content;
    }

    private static async Task<JsonElement> WaitForUploadCompletionAsync(HttpClient client, string uploadId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateAdminRequest(HttpMethod.Get, $"/api/v1/admin/import/uploads/{uploadId}/progress");
            using var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(500);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = ReadOperationStatus(document.RootElement.GetProperty("status"));

            if (status == OperationStatus.Completed)
            {
                return document.RootElement.Clone();
            }

            if (status is OperationStatus.Failed or OperationStatus.Cancelled)
            {
                var message = document.RootElement.TryGetProperty("errorMessage", out var error)
                    ? error.GetString()
                    : "Upload failed.";
                throw new InvalidOperationException($"Upload {uploadId} {status}: {message}");
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Upload {uploadId} did not complete within {timeout.TotalSeconds} seconds.");
    }

    private static async Task<JsonElement?> TryWaitForUploadCompletionAsync(HttpClient client, string uploadId, TimeSpan timeout)
    {
        try
        {
            return await WaitForUploadCompletionAsync(client, uploadId, timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<JsonElement?> TryGetUploadProgressAsync(HttpClient client, string uploadId)
    {
        using var request = CreateAdminRequest(HttpMethod.Get, $"/api/v1/admin/import/uploads/{uploadId}/progress");
        using var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForImportJobProgressAsync(HttpClient client, string jobId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateAdminRequest(HttpMethod.Get, $"/api/v1/admin/import/jobs/{jobId}");
            using var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(500);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = ReadImportStatus(document.RootElement.GetProperty("status"));

            if (status == ImportStatus.Completed)
            {
                return document.RootElement.Clone();
            }

            if (status is ImportStatus.Failed or ImportStatus.Cancelled)
            {
                var message = document.RootElement.TryGetProperty("errorMessage", out var error)
                    ? error.GetString()
                    : "Import failed.";
                throw new InvalidOperationException($"Import job {jobId} {status}: {message}");
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Import job {jobId} did not complete within {timeout.TotalSeconds} seconds.");
    }

    private static async Task<(string OperationId, JsonElement Response)> CreateDeployOperationAsync(
        HttpClient client,
        string targetId,
        string desiredRevision,
        string? currentRevision)
    {
        using var request = CreateAdminJsonRequest(HttpMethod.Post, "/api/v1/admin/deploy/operations", new
        {
            targetId,
            desiredRevision,
            currentRevision,
            reason = "Cloud validation deploy operation"
        });

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement.Clone();
        var operationId = root.GetProperty("operationId").GetString();
        operationId.Should().NotBeNullOrWhiteSpace();
        return (operationId!, root);
    }

    private static async Task<JsonElement> RequestRollbackAsync(HttpClient client, string operationId)
    {
        using var request = CreateAdminJsonRequest(HttpMethod.Post, $"/api/v1/admin/deploy/operations/{operationId}/rollback", new
        {
            reason = "Cloud validation rollback"
        });

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForDeployOperationTerminalStateAsync(HttpClient client, string operationId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateAdminRequest(HttpMethod.Get, $"/api/v1/admin/deploy/operations/{operationId}");
            using var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement.Clone();
            var status = root.GetProperty("status").GetString();

            if (status is "Succeeded" or "RolledBack")
            {
                return root;
            }

            if (status is "Failed" or "ManualInterventionRequired")
            {
                var phase = root.TryGetProperty("currentPhase", out var currentPhase)
                    ? currentPhase.GetString()
                    : null;
                var error = root.TryGetProperty("errorMessage", out var errorMessage)
                    ? errorMessage.GetString()
                    : null;
                throw new InvalidOperationException(
                    $"Deploy operation '{operationId}' entered terminal status '{status}': {phase ?? error ?? "unknown"}");
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"Deploy operation '{operationId}' did not reach a terminal state within {timeout.TotalSeconds} seconds.");
    }

    private static async Task<Guid> CreateSecureConnectionAsync(HttpClient client, string tableName)
    {
        var connectionName = BuildSecureConnectionName(tableName);

        using var request = CreateAdminJsonRequest(HttpMethod.Post, "/api/v1/admin/connections", new
        {
            name = connectionName,
            description = "Cloud deployment validation connection",
            host = GetRequiredEnv(PublishDbHostEnv),
            port = GetDbPort(),
            databaseName = GetRequiredEnv(PublishDbNameEnv),
            username = GetRequiredEnv(PublishDbUsernameEnv),
            password = GetRequiredEnv(PublishDbPasswordEnv),
            sslRequired = GetDbSslRequired(),
            sslMode = GetOptionalEnv(PublishDbSslModeEnv) ?? "Require"
        });

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {payload}");

        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("data")
            .GetProperty("connectionId")
            .GetGuid();
    }

    private static string BuildSecureConnectionName(string tableName)
    {
        const string prefix = "cloud-validation-";
        const int maxLength = 64;

        if (prefix.Length + tableName.Length <= maxLength)
        {
            return prefix + tableName;
        }

        const int preservedTailLength = 16;
        var maxTableNameLength = maxLength - prefix.Length;
        var tailLength = Math.Min(preservedTailLength, maxTableNameLength - 1);
        var headLength = maxTableNameLength - tailLength - 1;

        if (headLength <= 0)
        {
            return prefix + tableName[^maxTableNameLength..];
        }

        return $"{prefix}{tableName[..headLength]}-{tableName[^tailLength..]}";
    }

    private static async Task<ImportedTableHandle> DiscoverImportedTableAsync(
        HttpClient client,
        Guid connectionId,
        string tableName,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? lastPayload = null;
        HttpStatusCode? lastStatusCode = null;
        var expectedTableNames = BuildExpectedImportedTableNames(tableName);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateAdminRequest(HttpMethod.Get, $"/api/v1/admin/connections/{connectionId}/tables");
            using var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            lastPayload = payload;
            lastStatusCode = response.StatusCode;

            if (response.StatusCode == HttpStatusCode.InternalServerError)
            {
                await Task.Delay(1000);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

            using var document = JsonDocument.Parse(payload);
            var table = document.RootElement
                .GetProperty("tables")
                .EnumerateArray()
                .FirstOrDefault(candidate =>
                {
                    var candidateTableName = candidate.GetProperty("table").GetString();
                    return candidateTableName is not null &&
                        expectedTableNames.Contains(candidateTableName, StringComparer.OrdinalIgnoreCase);
                });

            if (table.ValueKind == JsonValueKind.Undefined)
            {
                await Task.Delay(1000);
                continue;
            }

            var geometryColumn = table.GetProperty("geometryColumn").GetString();
            geometryColumn.Should().NotBeNullOrWhiteSpace();

            var primaryKey = table.GetProperty("columns")
                .EnumerateArray()
                .FirstOrDefault(column => column.GetProperty("isPrimaryKey").GetBoolean())
                .GetProperty("name")
                .GetString();

            primaryKey.Should().NotBeNullOrWhiteSpace();

            var fields = table.GetProperty("columns")
                .EnumerateArray()
                .Select(column => column.GetProperty("name").GetString())
                .OfType<string>()
                .Where(column => !string.Equals(column, geometryColumn, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return new ImportedTableHandle(
                table.GetProperty("schema").GetString() ?? "public",
                table.GetProperty("table").GetString() ?? tableName,
                geometryColumn!,
                table.GetProperty("geometryType").GetString(),
                primaryKey!,
                fields);
        }

        throw new TimeoutException(
            $"Imported table '{tableName}' was not discoverable within {timeout.TotalSeconds} seconds. " +
            $"Last status: {(int?)lastStatusCode} {lastStatusCode}; last response: {lastPayload}");
    }

    private static HashSet<string> BuildExpectedImportedTableNames(string requestedTableName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            requestedTableName
        };

        var sanitized = Regex.Replace(requestedTableName, @"[^a-zA-Z0-9_]", "_").ToLowerInvariant();
        names.Add("imported_" + sanitized);

        return names;
    }

    private static async Task<PublishedLayerHandle> PublishImportedTableAsync(
        HttpClient client,
        Guid connectionId,
        ImportedTableHandle table,
        string tableName)
    {
        var serviceName = $"cloud_validation_{tableName[..Math.Min(tableName.Length, 40)]}";

        using var request = CreateAdminJsonRequest(HttpMethod.Post, $"/api/v1/admin/connections/{connectionId}/layers", new
        {
            schema = table.Schema,
            table = table.Table,
            layerName = $"Cloud Validation {tableName}",
            description = "Cloud deployment validation imported layer",
            geometryColumn = table.GeometryColumn,
            geometryType = ResolvePublishedGeometryType(table),
            primaryKey = table.PrimaryKey,
            fields = table.Fields,
            serviceName,
            enabled = true
        });

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {payload}");

        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        return new PublishedLayerHandle(
            data.GetProperty("serviceName").GetString() ?? serviceName,
            data.GetProperty("layerId").GetInt32());
    }

    private static async Task<JsonElement> WaitForPublishedServiceLayerAsync(
        HttpClient client,
        string serviceName,
        int layerId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? lastPayload = null;
        HttpStatusCode? lastStatusCode = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateOptionalApiKeyRequest(
                HttpMethod.Get,
                $"/rest/services/{serviceName}/FeatureServer?f=json");
            using var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            lastPayload = payload;
            lastStatusCode = response.StatusCode;

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError)
            {
                await Task.Delay(1000);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

            using var document = JsonDocument.Parse(payload);
            var layer = document.RootElement
                .GetProperty("layers")
                .EnumerateArray()
                .FirstOrDefault(candidate =>
                    candidate.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetInt32(out var candidateLayerId) &&
                    candidateLayerId == layerId);

            if (layer.ValueKind != JsonValueKind.Undefined)
            {
                return layer.Clone();
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException(
            $"Published service layer '{serviceName}/{layerId}' was not visible within {timeout.TotalSeconds} seconds. " +
            $"Last status: {(int?)lastStatusCode} {lastStatusCode}; last response: {lastPayload}");
    }

    private static async Task<JsonElement> WaitForPublishedLayerMetadataAsync(
        HttpClient client,
        string serviceName,
        int layerId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? lastPayload = null;
        HttpStatusCode? lastStatusCode = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateOptionalApiKeyRequest(
                HttpMethod.Get,
                $"/rest/services/{serviceName}/FeatureServer/{layerId}?f=json");
            using var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            lastPayload = payload;
            lastStatusCode = response.StatusCode;

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError)
            {
                await Task.Delay(1000);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var fields = root.GetProperty("fields");
            if (fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() > 0)
            {
                return root.Clone();
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException(
            $"Published layer metadata '{serviceName}/{layerId}' was not available within {timeout.TotalSeconds} seconds. " +
            $"Last status: {(int?)lastStatusCode} {lastStatusCode}; last response: {lastPayload}");
    }

    private static OperationStatus ReadOperationStatus(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse<OperationStatus>(element.GetString(), true, out var parsed)
                ? parsed
                : throw new InvalidOperationException("Upload operation status is invalid."),
            JsonValueKind.Number => element.TryGetInt32(out var value)
                ? (OperationStatus)value
                : throw new InvalidOperationException("Upload operation status is invalid."),
            _ => throw new InvalidOperationException("Upload operation status is invalid.")
        };

    private static ImportStatus ReadImportStatus(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse<ImportStatus>(element.GetString(), true, out var parsed)
                ? parsed
                : throw new InvalidOperationException("Import status is invalid."),
            JsonValueKind.Number => element.TryGetInt32(out var value)
                ? (ImportStatus)value
                : throw new InvalidOperationException("Import status is invalid."),
            _ => throw new InvalidOperationException("Import status is invalid.")
        };

    private static int GetImportTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(ImportTimeoutSecondsEnv);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 300;
    }

    private static bool TryGetEnv(string key, [NotNullWhen(true)] out string? value)
    {
        value = Environment.GetEnvironmentVariable(key);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static int GetDeployTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(DeployTimeoutSecondsEnv);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 180;
    }

    private static int GetDbPort()
    {
        var raw = GetOptionalEnv(PublishDbPortEnv);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 5432;
    }

    private static bool GetDbSslRequired()
    {
        var raw = GetOptionalEnv(PublishDbSslRequiredEnv);
        return !bool.TryParse(raw, out var parsed) || parsed;
    }

    private static string ResolvePublishedGeometryType(ImportedTableHandle table)
    {
        if (!string.IsNullOrWhiteSpace(table.GeometryType) &&
            !string.Equals(table.GeometryType, "GEOMETRY", StringComparison.OrdinalIgnoreCase))
        {
            return table.GeometryType;
        }

        return ImportedGeoJsonGeometryType;
    }

    private static string ResolveDesiredRevision(string? currentRevision)
    {
        var configured = GetOptionalEnv(DeployDesiredRevisionEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var platform = GetOptionalEnv(PlatformEnv);
        if (string.Equals(platform, "aws-lambda", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(currentRevision)
                ? currentRevision
                : "1";
        }

        return "cloud-validation";
    }

    private static string? GetOptionalEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool GetOptionalBool(string name, bool defaultValue = false)
    {
        var value = GetOptionalEnv(name);
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for cloud deployment validation tests.");
        }

        return value;
    }

    private sealed record ImportedTableHandle(
        string Schema,
        string Table,
        string GeometryColumn,
        string? GeometryType,
        string PrimaryKey,
        IReadOnlyList<string> Fields);

    private sealed record PublishedLayerHandle(string ServiceName, int LayerId);
}
