// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Honua.Server.Features.ControlPlane;

internal sealed record AzureFunctionsSiteConfigState
{
    public string? LinuxFxVersion { get; init; }
}

internal sealed record AzureFunctionsSlotSwapResult
{
    public required HttpStatusCode StatusCode { get; init; }

    public string? OperationLocation { get; init; }

    public int? RetryAfterSeconds { get; init; }
}

internal interface IAzureFunctionsSlotClient
{
    Task<AzureFunctionsSiteConfigState> GetSiteConfigAsync(
        string subscriptionId,
        string resourceGroupName,
        string functionAppName,
        string? slotName,
        CancellationToken cancellationToken = default);

    Task<AzureFunctionsSlotSwapResult> SwapSlotWithProductionAsync(
        string subscriptionId,
        string resourceGroupName,
        string functionAppName,
        string slotName,
        bool preserveVnet,
        CancellationToken cancellationToken = default);
}

internal sealed class AzureManagementFunctionsSlotClient(IHttpClientFactory httpClientFactory) : IAzureFunctionsSlotClient
{
    private const string ApiVersion = "2024-11-01";
    private static readonly Uri ManagementScope = new("https://management.azure.com/.default");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TokenCredential _credential = new DefaultAzureCredential();

    public async Task<AzureFunctionsSiteConfigState> GetSiteConfigAsync(
        string subscriptionId,
        string resourceGroupName,
        string functionAppName,
        string? slotName,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
                HttpMethod.Get,
                BuildSiteConfigUrl(subscriptionId, resourceGroupName, functionAppName, slotName),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new AzureFunctionsSiteConfigState
        {
            LinuxFxVersion = document.RootElement.TryGetProperty("properties", out var properties) &&
                             properties.TryGetProperty("linuxFxVersion", out var linuxFxVersion)
                ? linuxFxVersion.GetString()
                : null
        };
    }

    public async Task<AzureFunctionsSlotSwapResult> SwapSlotWithProductionAsync(
        string subscriptionId,
        string resourceGroupName,
        string functionAppName,
        string slotName,
        bool preserveVnet,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new AzureFunctionsSlotSwapRequest(slotName, preserveVnet),
            JsonSerializerOptions);
        using var request = await CreateRequestAsync(
                HttpMethod.Post,
                BuildSlotSwapUrl(subscriptionId, resourceGroupName, functionAppName),
                payload,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return new AzureFunctionsSlotSwapResult
        {
            StatusCode = response.StatusCode,
            OperationLocation = response.Headers.Location?.ToString() ?? GetHeaderValue(response.Headers, "Azure-AsyncOperation"),
            RetryAfterSeconds = response.Headers.RetryAfter?.Delta is { } retryAfter
                ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                : null
        };
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
            $"Azure Functions management request failed with status {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    private static string BuildSiteConfigUrl(
        string subscriptionId,
        string resourceGroupName,
        string functionAppName,
        string? slotName)
    {
        var basePath = $"/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.Web/sites/{Uri.EscapeDataString(functionAppName)}";
        var slotPath = string.IsNullOrWhiteSpace(slotName)
            ? basePath
            : $"{basePath}/slots/{Uri.EscapeDataString(slotName)}";
        return $"https://management.azure.com{slotPath}/config/web?api-version={ApiVersion}";
    }

    private static string BuildSlotSwapUrl(
        string subscriptionId,
        string resourceGroupName,
        string functionAppName)
        => $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.Web/sites/{Uri.EscapeDataString(functionAppName)}/slotsswap?api-version={ApiVersion}";

    private static string? GetHeaderValue(HttpResponseHeaders headers, string headerName)
        => headers.TryGetValues(headerName, out var values)
            ? values.FirstOrDefault()
            : null;

    private sealed record AzureFunctionsSlotSwapRequest(
        string TargetSlot,
        bool PreserveVnet);
}
