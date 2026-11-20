// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed partial class SensorThingsStreamEndpointsTests
{
    // One credential-bound observation stream for the authorization cases: posts paired tenant-a and
    // tenant-b observations and subscribes to datastream 1 over WebSocket or SSE.
    private sealed class ObservationStreamProof : IDisposable
    {
        internal const string Referer = "https://observation-proof.example/";
        internal const double MarkedResult = 73.25;

        private readonly WebAppFixture _fixture;
        private readonly CancellationToken _ct;
        private readonly IPortalTokenIssuer _issuer;
        private readonly HttpClient _tenantA;
        private readonly HttpClient _tenantB;
        private readonly List<string> _receivedFrames = [];
        private readonly List<double> _receivedResults = [];

        public ObservationStreamProof(WebAppFixture fixture, CancellationToken ct)
        {
            _fixture = fixture;
            _ct = ct;
            _issuer = fixture.GetService<IPortalTokenIssuer>();
            _tenantA = fixture.CreateAdminClient();
            _tenantA.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-a");
            _tenantB = fixture.CreateAdminClient();
            _tenantB.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-b");
        }

        public StreamReader? SseReader { get; private set; }

        public async Task<ExpiringCredential> IssueAsync(TimeSpan lifetime) => ExpiringCredential.From(await _issuer.IssueAsync(
            new PortalTokenIssueRequest("sta-proof", "STA proof", "tenant-a", ["reader"],
                PortalTokenClientType.Referer, Referer, DateTimeOffset.UtcNow + lifetime), _ct));

        public Task RevokeAsync(ExpiringCredential credential) => _issuer.RevokeAsync(credential.Token, _ct);

        public async Task<long> PublishAsync(double value)
        {
            using var excluded = await _tenantB.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations", new { result = -73001.5 }, _ct);
            excluded.StatusCode.Should().Be(HttpStatusCode.Created);
            using var included = await _tenantA.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations", new { result = value }, _ct);
            included.StatusCode.Should().Be(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await included.Content.ReadAsStringAsync(_ct));
            return body.RootElement.GetProperty("@iot.id").GetInt64();
        }

        public async Task<WebSocket> ConnectAsync(string token)
        {
            var client = _fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request =>
            {
                configure?.Invoke(request);
                request.Headers.Referer = Referer;
                request.Headers["X-Honua-Test-Schema"] = _fixture.CurrentSchema;
            };
            return await client.ConnectAsync(new Uri("ws://localhost" + Path(token)), _ct);
        }

        public static HttpRequestMessage SseRequest(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Path(token));
            request.Headers.Referrer = new Uri(Referer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            return request;
        }

        public async Task<JsonDocument> ReadObservationAsync(WebSocket socket)
        {
            while (true)
            {
                var frame = await ReadSocketFrameAsync(socket, _ct);
                _receivedFrames.Add(frame.RootElement.GetRawText());
                if (frame.RootElement.TryGetProperty("observations", out _)) { return frame; }
                frame.RootElement.TryGetProperty("code", out _).Should().BeFalse("authorization cannot end before the marked observation");
                frame.RootElement.GetRawText().Should().NotContain("tenant-b");
                frame.Dispose();
            }
        }

        // A retried setup connects live again, so it only sees the observation it published.
        public async Task<WebSocket> SubscribeWebSocketAsync(ExpiringCredential issued)
        {
            var opened = await ConnectAsync(issued.Token);
            try
            {
                using var connected = await ReadSocketFrameAsync(opened, _ct);
                _receivedFrames.Add(connected.RootElement.GetRawText());
                connected.RootElement.GetProperty("status").GetString().Should().Be("connected");
                var expectedId = await PublishAsync(MarkedResult);
                using var observation = await ReadObservationAsync(opened);
                AssertMarkedObservation(observation, expectedId);
                return opened;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }

        public async Task<HttpResponseMessage> SubscribeSseAsync(ExpiringCredential issued)
        {
            using var request = SseRequest(issued.Token);
            var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _ct);
            try
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var reader = new StreamReader(await response.Content.ReadAsStreamAsync(_ct));
                SseReader = reader;
                var status = await ReadUntilAsync(reader, "event: status", _ct);
                _receivedFrames.Add(status);
                status.Should().Contain("connected");
                var expectedId = await PublishAsync(MarkedResult);
                var pushed = await ReadUntilAsync(reader, "event: observations", _ct);
                _receivedFrames.Add(pushed);
                using var observation = JsonDocument.Parse(pushed[(pushed.IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
                AssertMarkedObservation(observation, expectedId);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        // A setup attempt the credential outlived is discarded, so what every attempt received is checked again.
        public void AssertEverySetupFrameWasClean()
        {
            _receivedFrames.Should().AllSatisfy(frame => frame.Should().NotContain("tenant-b"));
            _receivedResults.Should().AllSatisfy(result => result.Should().Be(MarkedResult));
        }

        public void Dispose()
        {
            _tenantA.Dispose();
            _tenantB.Dispose();
        }

        private static string Path(string token) => $"/sta/v1.1/ObservationsStream?datastreamId=1&token={token}";

        private void AssertMarkedObservation(JsonDocument observation, long expectedId)
        {
            _receivedResults.Add(observation.RootElement.GetProperty("observations")[0].GetProperty("result").GetDouble());
            observation.RootElement.GetProperty("observations")[0].GetProperty("result").GetDouble().Should().Be(MarkedResult);
            observation.RootElement.GetProperty("observations")[0].GetProperty("id").GetInt64().Should().Be(expectedId);
        }
    }
}
