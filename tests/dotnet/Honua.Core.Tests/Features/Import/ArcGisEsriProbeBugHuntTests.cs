// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Regression probes for the Esri REST migration client. These tests document source
/// behaviors observed during the 2026-09-03 ArcGIS compatibility hunt.
/// </summary>
public sealed class ArcGisEsriProbeBugHuntTests
{
    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> PublicResolver =
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    [Fact]
    public async Task TokenCredential_DoesNotPutSecretInRequestUrl()
    {
        var handler = new RecordingHandler("{\"serviceDescription\":\"Token test\",\"layers\":[]}");
        var client = CreateClient(handler);

        await client.DiscoverServiceAsync(
            ServiceUrl,
            timeoutSeconds: 5,
            maxRetries: 0,
            CancellationToken.None,
            new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessToken = "esri-secret-token"
            });

        Assert.NotNull(handler.LastRequestUri);
        Assert.DoesNotContain("esri-secret-token", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token=", handler.LastRequestUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer esri-secret-token", handler.LastEsriAuthorization);
    }

    [Fact]
    public async Task ServiceMetadata_200ErrorEnvelope_IsRejected()
    {
        var handler = new RecordingHandler("{\"error\":{\"code\":499,\"message\":\"Token required\"}}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverServiceAsync(
            ServiceUrl,
            timeoutSeconds: 5,
            maxRetries: 0,
            CancellationToken.None));
    }

    [Fact]
    public async Task LayerMetadata_200ErrorEnvelope_IsRejected()
    {
        var handler = new RecordingHandler("{\"error\":{\"code\":500,\"message\":\"layer unavailable\"}}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLayerInfoAsync(
            ServiceUrl,
            layerId: 0,
            timeoutSeconds: 5,
            maxRetries: 0,
            CancellationToken.None));
    }

    [Fact]
    public async Task LayerCount_200ErrorEnvelope_IsRejected()
    {
        var handler = new SequenceHandler(
            "{\"id\":0,\"name\":\"Parcels\",\"fields\":[]}",
            "{\"error\":{\"code\":500,\"message\":\"count unavailable\"}}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLayerInfoAsync(
            ServiceUrl,
            layerId: 0,
            timeoutSeconds: 5,
            maxRetries: 0,
            CancellationToken.None));
    }

    [Fact]
    public async Task Query_429RetryAfter_IsRetried()
    {
        var handler = new RetryAfterHandler();
        var client = CreateClient(handler);

        var result = await client.DiscoverServiceAsync(
            ServiceUrl,
            timeoutSeconds: 5,
            maxRetries: 1,
            CancellationToken.None);

        Assert.Equal("Recovered after throttling", result.ServiceName);
        Assert.Equal(2, handler.RequestCount);
    }

    private static ArcGisRestClient CreateClient(HttpMessageHandler handler)
        => new(new HttpClient(handler), NullLogger<ArcGisRestClient>.Instance, PublicResolver);

    private const string ServiceUrl =
        "https://example.com/arcgis/rest/services/Probe/FeatureServer";

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastEsriAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastEsriAuthorization = request.Headers.TryGetValues(
                "X-Esri-Authorization", out var values)
                ? values.Single()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SequenceHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requestCount) - 1;
            var body = responseBodies[Math.Min(index, responseBodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RetryAfterHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                var throttled = new HttpResponseMessage((HttpStatusCode)429);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(1));
                throttled.Content = new StringContent(
                    "{\"error\":{\"code\":429,\"message\":\"rate limited\"}}",
                    Encoding.UTF8,
                    "application/json");
                return Task.FromResult(throttled);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"serviceDescription\":\"Recovered after throttling\",\"layers\":[]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
