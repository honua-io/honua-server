// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared GeoServer fixture for integration tests that require a live GeoServer instance.
/// Uses Testcontainers to manage the GeoServer container lifecycle.
/// </summary>
public sealed class GeoServerFixture : IAsyncLifetime
{
    // The state object is process-wide and every mutation is serialized by _sharedLock.
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly GeoServerSharedState SharedState = new();

    private sealed class GeoServerSharedState
    {
        public IContainer? SharedContainer { get; set; }
        public string? SharedBaseUrl { get; set; }
        public string? SharedRestUrl { get; set; }
        public int SharedRefCount { get; set; }
        public bool SharedCuratedResourcesSeeded { get; set; }
        public bool SharedInitialized { get; set; }
    }

    private const string GeoServerImage = "docker.osgeo.org/geoserver:2.28.0";
    private const int GeoServerPort = 8080;
    private const string DefaultUsernameValue = "admin";
    private const string DefaultPasswordValue = "geoserver";
    private readonly bool _seedCuratedData;

    public const string CuratedWorkspaceName = "honua_curated";
    public const string EmptyWorkspaceName = "honua_empty";
    public const string CuratedDataStoreName = "tsunami";
    public const string CuratedLayerName = "Extreme_Tsunami_Evacuation_Zones";
    public const string CuratedWorkspaceStyleName = "honua_fill";
    public const string CuratedLayerGroupName = "honua_bundle";
    public const string CuratedAlternativeStyleName = "polygon";
    public const string CuratedQualifiedLayerName = $"{CuratedWorkspaceName}:{CuratedLayerName}";
    public const string CuratedQualifiedDefaultStyleName = $"{CuratedWorkspaceName}:{CuratedWorkspaceStyleName}";

    public GeoServerFixture(bool seedCuratedData = false)
    {
        _seedCuratedData = seedCuratedData;
    }

    public string BaseUrl => SharedState.SharedBaseUrl ?? throw new InvalidOperationException("GeoServer fixture not initialized.");

    public string RestUrl => SharedState.SharedRestUrl ?? throw new InvalidOperationException("GeoServer fixture not initialized.");

    public string Username => DefaultUsernameValue;

    public string Password => DefaultPasswordValue;

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!SharedState.SharedInitialized)
            {
                await StartContainerAsync().ConfigureAwait(false);
                SharedState.SharedInitialized = true;
            }

            if (_seedCuratedData && !SharedState.SharedCuratedResourcesSeeded)
            {
                await SeedCuratedResourcesAsync(
                    SharedState.SharedRestUrl ?? throw new InvalidOperationException("GeoServer REST URL not initialized.")).ConfigureAwait(false);
                SharedState.SharedCuratedResourcesSeeded = true;
            }

            SharedState.SharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (SharedState.SharedRefCount > 0)
            {
                SharedState.SharedRefCount--;
            }

            if (SharedState.SharedRefCount == 0 && SharedState.SharedInitialized)
            {
                if (SharedState.SharedContainer is not null)
                {
                    await SharedState.SharedContainer.DisposeAsync().ConfigureAwait(false);
                }

                SharedState.SharedContainer = null;
                SharedState.SharedBaseUrl = null;
                SharedState.SharedRestUrl = null;
                SharedState.SharedCuratedResourcesSeeded = false;
                SharedState.SharedInitialized = false;
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    private static async Task StartContainerAsync()
    {
        SharedState.SharedContainer = new ContainerBuilder()
            .WithImage(GeoServerImage)
            .WithPortBinding(GeoServerPort, true)
            .Build();

        await SharedState.SharedContainer.StartAsync().ConfigureAwait(false);

        var port = SharedState.SharedContainer.GetMappedPublicPort(GeoServerPort);
        SharedState.SharedBaseUrl = $"http://127.0.0.1:{port}";
        SharedState.SharedRestUrl = $"{SharedState.SharedBaseUrl}/geoserver/rest";

        await WaitForGeoServerReadyAsync(SharedState.SharedBaseUrl, SharedState.SharedRestUrl).ConfigureAwait(false);
    }

    private static async Task WaitForGeoServerReadyAsync(string baseUrl, string restUrl)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(8);
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (!await IsWebAppReadyAsync(httpClient, baseUrl).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    continue;
                }

                var statusCode = await GetRestStatusAsync(httpClient, restUrl).ConfigureAwait(false);
                if (statusCode == HttpStatusCode.OK)
                {
                    return;
                }

                if (statusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException("GeoServer is running, but the default admin/geoserver credentials were rejected.");
                }
            }
            catch (Exception ex) when (IsTransientStartupFailure(ex))
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        var errorDetails = lastError is null ? string.Empty : $" Last error: {lastError.Message}";
        throw new TimeoutException($"Timed out waiting for GeoServer to become ready.{errorDetails}");
    }

    private static async Task<bool> IsWebAppReadyAsync(HttpClient httpClient, string baseUrl)
    {
        using var response = await httpClient.GetAsync($"{baseUrl}/geoserver/web/").ConfigureAwait(false);
        return response.IsSuccessStatusCode ||
               response.StatusCode == HttpStatusCode.Found ||
               response.StatusCode == HttpStatusCode.MovedPermanently;
    }

    private static async Task<HttpStatusCode> GetRestStatusAsync(HttpClient httpClient, string restUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{restUrl}/workspaces.json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{DefaultUsernameValue}:{DefaultPasswordValue}")));

        using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }

    private static bool IsTransientStartupFailure(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or TimeoutException;

    private static async Task SeedCuratedResourcesAsync(string restUrl)
    {
        using var httpClient = CreateGeoServerHttpClient();

        await EnsureWorkspaceExistsAsync(httpClient, restUrl, EmptyWorkspaceName).ConfigureAwait(false);
        await EnsureWorkspaceExistsAsync(httpClient, restUrl, CuratedWorkspaceName).ConfigureAwait(false);
        await EnsureCuratedDataStoreExistsAsync(httpClient, restUrl).ConfigureAwait(false);
        await EnsureWorkspaceStyleExistsAsync(httpClient, restUrl).ConfigureAwait(false);
        await ConfigureCuratedLayerStylesAsync(httpClient, restUrl).ConfigureAwait(false);
        await EnsureWorkspaceLayerGroupExistsAsync(httpClient, restUrl).ConfigureAwait(false);
    }

    private static HttpClient CreateGeoServerHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{DefaultUsernameValue}:{DefaultPasswordValue}")));

        return httpClient;
    }

    private static async Task EnsureWorkspaceExistsAsync(HttpClient httpClient, string restUrl, string workspaceName)
    {
        if (await ResourceExistsAsync(httpClient, $"{restUrl}/workspaces/{EscapePathSegment(workspaceName)}.json").ConfigureAwait(false))
        {
            return;
        }

        using var content = CreateJsonContent($"{{\"workspace\":{{\"name\":\"{workspaceName}\"}}}}");
        using var response = await httpClient.PostAsync($"{restUrl}/workspaces", content).ConfigureAwait(false);
        await EnsureExpectedStatusCodeAsync(response, HttpStatusCode.Created).ConfigureAwait(false);
    }

    private static async Task EnsureCuratedDataStoreExistsAsync(HttpClient httpClient, string restUrl)
    {
        var dataStoreUrl = $"{restUrl}/workspaces/{CuratedWorkspaceName}/datastores/{CuratedDataStoreName}.json";
        if (await ResourceExistsAsync(httpClient, dataStoreUrl).ConfigureAwait(false))
        {
            return;
        }

        var shapefileZipPath = ReferenceServerTestData.ResolveTsunamiEvacuationZonesZipPath();
        var zipBytes = await File.ReadAllBytesAsync(shapefileZipPath).ConfigureAwait(false);

        using var content = new ByteArrayContent(zipBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var response = await httpClient.PutAsync(
            $"{restUrl}/workspaces/{CuratedWorkspaceName}/datastores/{CuratedDataStoreName}/file.shp?configure=all",
            content).ConfigureAwait(false);

        await EnsureExpectedStatusCodeAsync(response, HttpStatusCode.Created, HttpStatusCode.OK).ConfigureAwait(false);
    }

    private static async Task EnsureWorkspaceStyleExistsAsync(HttpClient httpClient, string restUrl)
    {
        var styleUrl = $"{restUrl}/workspaces/{CuratedWorkspaceName}/styles/{CuratedWorkspaceStyleName}.json";
        if (await ResourceExistsAsync(httpClient, styleUrl).ConfigureAwait(false))
        {
            return;
        }

        using var content = new StringContent(CuratedPolygonStyleSld, Encoding.UTF8, "application/vnd.ogc.sld+xml");
        using var response = await httpClient.PostAsync(
            $"{restUrl}/workspaces/{CuratedWorkspaceName}/styles?name={CuratedWorkspaceStyleName}",
            content).ConfigureAwait(false);

        await EnsureExpectedStatusCodeAsync(response, HttpStatusCode.Created).ConfigureAwait(false);
    }

    private static async Task ConfigureCuratedLayerStylesAsync(HttpClient httpClient, string restUrl)
    {
        using var content = CreateJsonContent(
            $"{{\"layer\":{{\"defaultStyle\":{{\"name\":\"{CuratedWorkspaceStyleName}\",\"workspace\":\"{CuratedWorkspaceName}\"}},\"styles\":{{\"style\":[{{\"name\":\"{CuratedAlternativeStyleName}\"}}]}}}}}}");
        using var response = await httpClient.PutAsync(
            $"{restUrl}/layers/{EscapePathSegment(CuratedQualifiedLayerName)}.json",
            content).ConfigureAwait(false);

        await EnsureExpectedStatusCodeAsync(response, HttpStatusCode.OK).ConfigureAwait(false);
    }

    private static async Task EnsureWorkspaceLayerGroupExistsAsync(HttpClient httpClient, string restUrl)
    {
        var layerGroupUrl = $"{restUrl}/workspaces/{CuratedWorkspaceName}/layergroups/{CuratedLayerGroupName}.json";
        if (await ResourceExistsAsync(httpClient, layerGroupUrl).ConfigureAwait(false))
        {
            return;
        }

        using var content = CreateJsonContent(
            $"{{\"layerGroup\":{{\"name\":\"{CuratedLayerGroupName}\",\"mode\":\"SINGLE\",\"title\":\"Honua Curated Bundle\",\"publishables\":{{\"published\":[{{\"@type\":\"layer\",\"name\":\"{CuratedQualifiedLayerName}\"}}]}},\"styles\":{{\"style\":[\"\"]}}}}}}");
        using var response = await httpClient.PostAsync(
            $"{restUrl}/workspaces/{CuratedWorkspaceName}/layergroups",
            content).ConfigureAwait(false);

        await EnsureExpectedStatusCodeAsync(response, HttpStatusCode.Created).ConfigureAwait(false);
    }

    private static async Task<bool> ResourceExistsAsync(HttpClient httpClient, string url)
    {
        using var response = await httpClient.GetAsync(url).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            _ => throw new InvalidOperationException(
                $"Unexpected GeoServer response checking '{url}': {(int)response.StatusCode} {response.ReasonPhrase}")
        };
    }

    private static async Task EnsureExpectedStatusCodeAsync(
        HttpResponseMessage response,
        HttpStatusCode primaryExpected,
        HttpStatusCode? alternateExpected = null)
    {
        if (response.StatusCode == primaryExpected || response.StatusCode == alternateExpected)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            $"GeoServer seed request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
    }

    private static StringContent CreateJsonContent(string payload)
        => new(payload, Encoding.UTF8, "application/json");

    private static string EscapePathSegment(string value)
        => Uri.EscapeDataString(value);

    private const string CuratedPolygonStyleSld = """
        <?xml version="1.0" encoding="UTF-8"?>
        <StyledLayerDescriptor version="1.0.0"
            xmlns="http://www.opengis.net/sld"
            xmlns:ogc="http://www.opengis.net/ogc"
            xmlns:xlink="http://www.w3.org/1999/xlink"
            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
            xsi:schemaLocation="http://www.opengis.net/sld StyledLayerDescriptor.xsd">
          <NamedLayer>
            <Name>honua_fill</Name>
            <UserStyle>
              <Title>Honua Fill</Title>
              <FeatureTypeStyle>
                <Rule>
                  <PolygonSymbolizer>
                    <Fill>
                      <CssParameter name="fill">#f36f21</CssParameter>
                    </Fill>
                    <Stroke>
                      <CssParameter name="stroke">#1f2937</CssParameter>
                    </Stroke>
                  </PolygonSymbolizer>
                </Rule>
              </FeatureTypeStyle>
            </UserStyle>
          </NamedLayer>
        </StyledLayerDescriptor>
        """;
}
