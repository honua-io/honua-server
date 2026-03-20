// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Traffic weight entry for an Azure Container Apps revision.
/// </summary>
internal sealed record AzureContainerAppsTrafficWeight
{
    /// <summary>
    /// Revision name receiving this weight. Null when <see cref="LatestRevision"/> is true.
    /// </summary>
    public string? RevisionName { get; init; }

    /// <summary>
    /// Whether this weight targets the latest ready revision.
    /// </summary>
    public bool LatestRevision { get; init; }

    /// <summary>
    /// Percentage of traffic routed to this revision (0–100).
    /// </summary>
    public int Weight { get; init; }

    /// <summary>
    /// Optional traffic label such as "stable" or "canary".
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>
/// Summary of a single Container Apps revision from the revisions list.
/// </summary>
internal sealed record AzureContainerAppsRevisionSummary
{
    /// <summary>
    /// Revision name.
    /// </summary>
    public required string RevisionName { get; init; }

    /// <summary>
    /// Whether the revision is currently active and can receive traffic.
    /// </summary>
    public bool Active { get; init; }

    /// <summary>
    /// Percentage of traffic currently routed to this revision.
    /// </summary>
    public int TrafficWeight { get; init; }
}

/// <summary>
/// Current ingress traffic configuration and active revision list for a Container App.
/// </summary>
internal sealed record AzureContainerAppsTrafficState
{
    /// <summary>
    /// Current traffic weight configuration.
    /// </summary>
    public IReadOnlyList<AzureContainerAppsTrafficWeight> Traffic { get; init; } =
        Array.Empty<AzureContainerAppsTrafficWeight>();

    /// <summary>
    /// Active revisions for this Container App.
    /// </summary>
    public IReadOnlyList<AzureContainerAppsRevisionSummary> Revisions { get; init; } =
        Array.Empty<AzureContainerAppsRevisionSummary>();
}

/// <summary>
/// Health and provisioning state for a specific Container Apps revision.
/// </summary>
internal sealed record AzureContainerAppsRevisionState
{
    /// <summary>
    /// Revision name.
    /// </summary>
    public required string RevisionName { get; init; }

    /// <summary>
    /// Provisioning state: Provisioning, Provisioned, Failed, Deprovisioning.
    /// </summary>
    public string? ProvisioningState { get; init; }

    /// <summary>
    /// Running state: Running, Processing, Stopped, Degraded, Failed.
    /// </summary>
    public string? RunningState { get; init; }

    /// <summary>
    /// Whether the revision is active and can receive traffic.
    /// </summary>
    public bool Active { get; init; }

    /// <summary>
    /// Health state: Healthy, Unhealthy, None.
    /// </summary>
    public string? HealthState { get; init; }

    /// <summary>
    /// Fully qualified domain name when available.
    /// </summary>
    public string? Fqdn { get; init; }
}

/// <summary>
/// Result of updating traffic weights for a Container App.
/// </summary>
internal sealed record AzureContainerAppsTrafficUpdateResult
{
    /// <summary>
    /// HTTP status code returned by the ARM API.
    /// </summary>
    public required HttpStatusCode StatusCode { get; init; }
}

/// <summary>
/// ARM REST API client for Azure Container Apps revision and traffic management.
/// </summary>
internal interface IAzureContainerAppsRevisionClient
{
    /// <summary>
    /// Gets current ingress traffic configuration and active revision list.
    /// </summary>
    Task<AzureContainerAppsTrafficState> GetTrafficStateAsync(
        string subscriptionId, string resourceGroupName, string appName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets health and provisioning status for a specific revision.
    /// </summary>
    Task<AzureContainerAppsRevisionState> GetRevisionAsync(
        string subscriptionId, string resourceGroupName, string appName,
        string revisionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates ingress traffic weights between revisions.
    /// </summary>
    Task<AzureContainerAppsTrafficUpdateResult> UpdateTrafficAsync(
        string subscriptionId, string resourceGroupName, string appName,
        IReadOnlyList<AzureContainerAppsTrafficWeight> weights,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates a deactivated revision so it can receive traffic.
    /// </summary>
    Task ActivateRevisionAsync(
        string subscriptionId, string resourceGroupName, string appName,
        string revisionName, CancellationToken cancellationToken = default);
}

/// <summary>
/// ARM REST API client for Azure Container Apps using direct HTTP calls to management.azure.com.
/// </summary>
internal sealed class AzureManagementContainerAppsRevisionClient(IHttpClientFactory httpClientFactory)
    : IAzureContainerAppsRevisionClient
{
    private const string ApiVersion = "2024-03-01";
    private static readonly Uri ManagementScope = new("https://management.azure.com/.default");
    private readonly TokenCredential _credential = new DefaultAzureCredential();

    public async Task<AzureContainerAppsTrafficState> GetTrafficStateAsync(
        string subscriptionId, string resourceGroupName, string appName,
        CancellationToken cancellationToken = default)
    {
        using var appRequest = await CreateRequestAsync(
                HttpMethod.Get,
                BuildContainerAppUrl(subscriptionId, resourceGroupName, appName),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var appResponse = await client.SendAsync(appRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(appResponse, cancellationToken).ConfigureAwait(false);

        await using var appStream = await appResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var appDoc = await JsonDocument.ParseAsync(appStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var traffic = ParseTrafficWeights(appDoc.RootElement);

        using var revisionsRequest = await CreateRequestAsync(
                HttpMethod.Get,
                BuildRevisionsListUrl(subscriptionId, resourceGroupName, appName),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var revisionsResponse = await client.SendAsync(revisionsRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(revisionsResponse, cancellationToken).ConfigureAwait(false);

        await using var revisionsStream = await revisionsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var revisionsDoc = await JsonDocument.ParseAsync(revisionsStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var revisions = ParseRevisionSummaries(revisionsDoc.RootElement, traffic);

        return new AzureContainerAppsTrafficState
        {
            Traffic = traffic,
            Revisions = revisions
        };
    }

    public async Task<AzureContainerAppsRevisionState> GetRevisionAsync(
        string subscriptionId, string resourceGroupName, string appName,
        string revisionName, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
                HttpMethod.Get,
                BuildRevisionUrl(subscriptionId, resourceGroupName, appName, revisionName),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return ParseRevisionState(document.RootElement);
    }

    public async Task<AzureContainerAppsTrafficUpdateResult> UpdateTrafficAsync(
        string subscriptionId, string resourceGroupName, string appName,
        IReadOnlyList<AzureContainerAppsTrafficWeight> weights,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildTrafficPatchPayload(weights);
        using var request = await CreateRequestAsync(
                HttpMethod.Patch,
                BuildContainerAppUrl(subscriptionId, resourceGroupName, appName),
                payload,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return new AzureContainerAppsTrafficUpdateResult
        {
            StatusCode = response.StatusCode
        };
    }

    public async Task ActivateRevisionAsync(
        string subscriptionId, string resourceGroupName, string appName,
        string revisionName, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
                HttpMethod.Post,
                BuildRevisionActivateUrl(subscriptionId, resourceGroupName, appName, revisionName),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string url,
        byte[]? payload,
        CancellationToken cancellationToken)
    {
        var accessToken = await _credential.GetTokenAsync(
                new TokenRequestContext([ManagementScope.ToString()]),
                cancellationToken)
            .ConfigureAwait(false);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        if (payload != null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = Encoding.UTF8.WebName
            };
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Azure Container Apps management request failed with status {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    private static List<AzureContainerAppsTrafficWeight> ParseTrafficWeights(JsonElement root)
    {
        var weights = new List<AzureContainerAppsTrafficWeight>();
        if (!root.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("configuration", out var configuration) ||
            !configuration.TryGetProperty("ingress", out var ingress) ||
            !ingress.TryGetProperty("traffic", out var traffic) ||
            traffic.ValueKind != JsonValueKind.Array)
        {
            return weights;
        }

        foreach (var entry in traffic.EnumerateArray())
        {
            weights.Add(new AzureContainerAppsTrafficWeight
            {
                RevisionName = entry.TryGetProperty("revisionName", out var rn) ? rn.GetString() : null,
                LatestRevision = entry.TryGetProperty("latestRevision", out var lr) && lr.GetBoolean(),
                Weight = entry.TryGetProperty("weight", out var w) ? w.GetInt32() : 0,
                Label = entry.TryGetProperty("label", out var l) ? l.GetString() : null
            });
        }

        return weights;
    }

    private static List<AzureContainerAppsRevisionSummary> ParseRevisionSummaries(
        JsonElement root,
        List<AzureContainerAppsTrafficWeight> trafficWeights)
    {
        var revisions = new List<AzureContainerAppsRevisionSummary>();
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return revisions;
        }

        foreach (var entry in value.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var active = entry.TryGetProperty("properties", out var props) &&
                         props.TryGetProperty("active", out var a) && a.GetBoolean();

            var weight = 0;
            foreach (var tw in trafficWeights)
            {
                if (string.Equals(tw.RevisionName, name, StringComparison.OrdinalIgnoreCase))
                {
                    weight = tw.Weight;
                    break;
                }
            }

            revisions.Add(new AzureContainerAppsRevisionSummary
            {
                RevisionName = name,
                Active = active,
                TrafficWeight = weight
            });
        }

        return revisions;
    }

    private static AzureContainerAppsRevisionState ParseRevisionState(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var properties = root.TryGetProperty("properties", out var props) ? props : default;

        return new AzureContainerAppsRevisionState
        {
            RevisionName = name,
            ProvisioningState = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("provisioningState", out var ps)
                ? ps.GetString() : null,
            RunningState = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("runningState", out var rs)
                ? rs.GetString() : null,
            Active = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("active", out var a) && a.GetBoolean(),
            HealthState = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("healthState", out var hs)
                ? hs.GetString() : null,
            Fqdn = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("fqdn", out var fqdn)
                ? fqdn.GetString() : null
        };
    }

    private static byte[] BuildTrafficPatchPayload(IReadOnlyList<AzureContainerAppsTrafficWeight> weights)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("configuration");
        writer.WriteStartObject("ingress");
        writer.WriteStartArray("traffic");

        foreach (var weight in weights)
        {
            writer.WriteStartObject();
            if (weight.LatestRevision)
            {
                writer.WriteBoolean("latestRevision", true);
            }
            else if (!string.IsNullOrWhiteSpace(weight.RevisionName))
            {
                writer.WriteString("revisionName", weight.RevisionName);
            }

            writer.WriteNumber("weight", weight.Weight);
            if (!string.IsNullOrWhiteSpace(weight.Label))
            {
                writer.WriteString("label", weight.Label);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
    }

    private static string BuildContainerAppUrl(string subscriptionId, string resourceGroupName, string appName)
        => $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(appName)}?api-version={ApiVersion}";

    private static string BuildRevisionsListUrl(string subscriptionId, string resourceGroupName, string appName)
        => $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(appName)}/revisions?api-version={ApiVersion}";

    private static string BuildRevisionUrl(string subscriptionId, string resourceGroupName, string appName, string revisionName)
        => $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(appName)}/revisions/{Uri.EscapeDataString(revisionName)}?api-version={ApiVersion}";

    private static string BuildRevisionActivateUrl(string subscriptionId, string resourceGroupName, string appName, string revisionName)
        => $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(appName)}/revisions/{Uri.EscapeDataString(revisionName)}/activate?api-version={ApiVersion}";
}
