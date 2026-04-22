// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit.Sdk;

namespace Honua.Server.Tests.Scale;

/// <summary>
/// Multi-node scale tests that validate replica state is shared across instances.
/// </summary>
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerReplicaScaleTests
{
    private const string BaseUrlEnv = "HONUA_SCALE_TEST_BASE_URL";
    private const string AdminApiKeyEnv = "HONUA_SCALE_TEST_ADMIN_API_KEY";
    private const string ServiceIdEnv = "HONUA_SCALE_TEST_SERVICE_ID";
    private const string ApiKeyHeader = "X-API-Key";
    private const string InstanceHeader = "X-Instance-ID";
    private const int MaxCrossNodeAttempts = 10;

    [ScaleTest(BaseUrlEnv, AdminApiKeyEnv)]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task CreateReplica_ExtractChangesOnDifferentInstance_ReturnsReplicaState()
    {
        using var client = CreateClient();
        var apiKey = GetRequiredEnv(AdminApiKeyEnv);
        var serviceId = await ResolveServiceIdAsync(client);
        var createdReplicaIds = new List<string>();
        var routes = new List<string>();

        try
        {
            for (var attempt = 1; attempt <= MaxCrossNodeAttempts; attempt++)
            {
                var create = await CreateReplicaAsync(client, serviceId, apiKey);
                create.StatusCode.Should().Be(HttpStatusCode.OK);

                var replicaId = GetRequiredStringProperty(create.Content, "replicaID");
                createdReplicaIds.Add(replicaId);

                var extract = await ExtractChangesAsync(client, serviceId, apiKey, replicaId);
                extract.StatusCode.Should().Be(HttpStatusCode.OK);
                routes.Add($"{create.InstanceId}->{extract.InstanceId}");

                if (string.Equals(create.InstanceId, extract.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(extract.Content);
                var root = document.RootElement;
                root.GetProperty("success").GetBoolean().Should().BeTrue();
                root.GetProperty("replicaID").GetString().Should().Be(replicaId);
                root.GetProperty("layerChanges").ValueKind.Should().Be(JsonValueKind.Array);
                return;
            }

            throw new XunitException(
                $"Failed to route createReplica/extractChanges to different instances in {MaxCrossNodeAttempts} attempts. Observed routes: {string.Join(", ", routes)}");
        }
        finally
        {
            await CleanupReplicasAsync(client, serviceId, apiKey, createdReplicaIds);
        }
    }

    [ScaleTest(BaseUrlEnv, AdminApiKeyEnv)]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task SynchronizeReplica_OnDifferentInstance_UpdatesReplicaStateClusterWide()
    {
        using var client = CreateClient();
        var apiKey = GetRequiredEnv(AdminApiKeyEnv);
        var serviceId = await ResolveServiceIdAsync(client);
        var createdReplicaIds = new List<string>();
        var routes = new List<string>();

        try
        {
            for (var attempt = 1; attempt <= MaxCrossNodeAttempts; attempt++)
            {
                var create = await CreateReplicaAsync(client, serviceId, apiKey);
                create.StatusCode.Should().Be(HttpStatusCode.OK);

                var replicaId = GetRequiredStringProperty(create.Content, "replicaID");
                createdReplicaIds.Add(replicaId);

                var sync = await SynchronizeReplicaAsync(client, serviceId, apiKey, replicaId);
                sync.StatusCode.Should().Be(HttpStatusCode.OK);
                routes.Add($"{create.InstanceId}->{sync.InstanceId}");

                if (string.Equals(create.InstanceId, sync.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using (var syncDocument = JsonDocument.Parse(sync.Content))
                {
                    var syncRoot = syncDocument.RootElement;
                    syncRoot.GetProperty("success").GetBoolean().Should().BeTrue();
                    syncRoot.GetProperty("replicaID").GetString().Should().Be(replicaId);
                }

                var extract = await ExtractChangesAsync(client, serviceId, apiKey, replicaId);
                extract.StatusCode.Should().Be(HttpStatusCode.OK);

                using var extractDocument = JsonDocument.Parse(extract.Content);
                var extractRoot = extractDocument.RootElement;
                extractRoot.GetProperty("success").GetBoolean().Should().BeTrue();

                var layerChanges = extractRoot.GetProperty("layerChanges");
                layerChanges.GetArrayLength().Should().BeGreaterThan(0);

                foreach (var layerChange in layerChanges.EnumerateArray())
                {
                    layerChange.GetProperty("adds").GetInt32().Should().Be(0);
                    layerChange.GetProperty("updates").GetInt32().Should().Be(0);
                    layerChange.GetProperty("deletes").GetInt32().Should().Be(0);
                }

                return;
            }

            throw new XunitException(
                $"Failed to route createReplica/synchronizeReplica to different instances in {MaxCrossNodeAttempts} attempts. Observed routes: {string.Join(", ", routes)}");
        }
        finally
        {
            await CleanupReplicasAsync(client, serviceId, apiKey, createdReplicaIds);
        }
    }

    [ScaleTest(BaseUrlEnv, AdminApiKeyEnv)]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task UnRegisterReplica_OnDifferentInstance_RemovesReplicaAcrossCluster()
    {
        using var client = CreateClient();
        var apiKey = GetRequiredEnv(AdminApiKeyEnv);
        var serviceId = await ResolveServiceIdAsync(client);
        var outstandingReplicaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<string>();

        try
        {
            for (var attempt = 1; attempt <= MaxCrossNodeAttempts; attempt++)
            {
                var create = await CreateReplicaAsync(client, serviceId, apiKey);
                create.StatusCode.Should().Be(HttpStatusCode.OK);

                var replicaId = GetRequiredStringProperty(create.Content, "replicaID");
                outstandingReplicaIds.Add(replicaId);

                var unregister = await UnregisterReplicaAsync(client, serviceId, apiKey, replicaId);
                routes.Add($"{create.InstanceId}->{unregister.InstanceId}:{(int)unregister.StatusCode}");

                if (unregister.StatusCode == HttpStatusCode.OK)
                {
                    outstandingReplicaIds.Remove(replicaId);
                }

                if (unregister.StatusCode != HttpStatusCode.OK ||
                    string.Equals(create.InstanceId, unregister.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var extract = await ExtractChangesAsync(client, serviceId, apiKey, replicaId);
                extract.StatusCode.Should().Be(HttpStatusCode.NotFound);
                return;
            }

            throw new XunitException(
                $"Failed to unregister a replica from a different instance in {MaxCrossNodeAttempts} attempts. Observed routes/statuses: {string.Join(", ", routes)}");
        }
        finally
        {
            await CleanupReplicasAsync(client, serviceId, apiKey, outstandingReplicaIds);
        }
    }

    private static async Task CleanupReplicasAsync(
        HttpClient client,
        string serviceId,
        string apiKey,
        IEnumerable<string> replicaIds)
    {
        foreach (var replicaId in replicaIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await UnregisterReplicaAsync(client, serviceId, apiKey, replicaId);
        }
    }

    private static async Task<ScaleResponse> CreateReplicaAsync(HttpClient client, string serviceId, string apiKey)
    {
        var payload = new
        {
            replicaName = $"scale-replica-{Guid.NewGuid():N}",
            syncModel = "perReplica",
            f = "json"
        };

        var path = $"/rest/services/{Uri.EscapeDataString(serviceId)}/FeatureServer/createReplica";
        return await SendAuthenticatedPostAsync(client, path, payload, apiKey);
    }

    private static async Task<ScaleResponse> ExtractChangesAsync(
        HttpClient client,
        string serviceId,
        string apiKey,
        string replicaId)
    {
        var payload = new
        {
            replicaID = replicaId,
            f = "json"
        };

        var path = $"/rest/services/{Uri.EscapeDataString(serviceId)}/FeatureServer/extractChanges";
        return await SendAuthenticatedPostAsync(client, path, payload, apiKey);
    }

    private static async Task<ScaleResponse> SynchronizeReplicaAsync(
        HttpClient client,
        string serviceId,
        string apiKey,
        string replicaId)
    {
        var payload = new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        };

        var path = $"/rest/services/{Uri.EscapeDataString(serviceId)}/FeatureServer/synchronizeReplica";
        return await SendAuthenticatedPostAsync(client, path, payload, apiKey);
    }

    private static async Task<ScaleResponse> UnregisterReplicaAsync(
        HttpClient client,
        string serviceId,
        string apiKey,
        string replicaId)
    {
        var payload = new
        {
            replicaID = replicaId,
            f = "json"
        };

        var path = $"/rest/services/{Uri.EscapeDataString(serviceId)}/FeatureServer/unRegisterReplica";
        return await SendAuthenticatedPostAsync(client, path, payload, apiKey);
    }

    private static async Task<ScaleResponse> SendAuthenticatedPostAsync(
        HttpClient client,
        string path,
        object payload,
        string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ConnectionClose = true;
        request.Headers.Add(ApiKeyHeader, apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var content = await response.Content.ReadAsStringAsync();
        var instanceId = GetRequiredInstanceId(response);

        return new ScaleResponse(response.StatusCode, instanceId, content);
    }

    private static async Task<string> ResolveServiceIdAsync(HttpClient client)
    {
        var configuredServiceId = Environment.GetEnvironmentVariable(ServiceIdEnv);
        if (!string.IsNullOrWhiteSpace(configuredServiceId))
        {
            return configuredServiceId.Trim();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services?f=json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ConnectionClose = true;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var services = document.RootElement.GetProperty("services");

        foreach (var service in services.EnumerateArray())
        {
            var type = GetRequiredStringProperty(service, "type");
            if (!string.Equals(type, "FeatureServer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return GetRequiredStringProperty(service, "name");
        }

        throw new InvalidOperationException(
            "Scale test environment did not return any FeatureServer services from /rest/services.");
    }

    private static string GetRequiredInstanceId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(InstanceHeader, out var values))
        {
            throw new InvalidOperationException($"Missing '{InstanceHeader}' response header.");
        }

        var instanceId = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new InvalidOperationException($"Response header '{InstanceHeader}' is empty.");
        }

        return instanceId;
    }

    private static string GetRequiredStringProperty(string content, string propertyName)
    {
        using var document = JsonDocument.Parse(content);
        return GetRequiredStringProperty(document.RootElement, propertyName);
    }

    private static string GetRequiredStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"Response payload is missing required property '{propertyName}'.");
        }

        var stringValue = value.GetString();
        if (string.IsNullOrWhiteSpace(stringValue))
        {
            throw new InvalidOperationException($"Response payload property '{propertyName}' was null or empty.");
        }

        return stringValue;
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

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for scale tests.");
        }

        return value;
    }

    private sealed record ScaleResponse(HttpStatusCode StatusCode, string InstanceId, string Content);
}
