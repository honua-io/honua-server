// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// honua-release#202: the GeoServices GP surface on a Redis-less install. Redis is optional
/// (PostGIS is not), so submitting a job without a durable job store must be refused up front
/// with a machine-readable receipt rather than hanging or accepting a job that can never drain.
/// The Esri error envelope has no extension members, so the receipt rides the
/// <c>error.details[]</c> array using its existing <c>Key: value</c> convention.
/// </summary>
/// <remarks>
/// The <see cref="WebAppFixture"/> runs without Redis and registers no
/// <c>IExecutionJobStore</c> — that is the degraded composition under test.
/// <c>GPServerEndpointTests</c> substitutes an in-memory store to cover the composed path.
/// </remarks>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerDegradedJobStoreTests : IAsyncLifetime
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ServiceId = WebAppFixture.TestServiceId;

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithoutDurableJobStore_ReturnsCapabilityUnavailableReceiptInDetails()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "25.5",
        });

        using var response = await _client.PostAsync(
            $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob",
            content);

        // Esri GeoServices signals errors in the body, not the HTTP status.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(503);

        var details = error.GetProperty("details").EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        details.Should().Contain($"code: {CapabilityUnavailableCodes.ErrorCode}");
        details.Should().Contain($"missingDependency: {CapabilityUnavailableCodes.RedisDependency}");
        details.Should().Contain($"capability: {CapabilityUnavailableCodes.DurableJobsCapability}");
        details.Should().Contain($"remediationRef: {CapabilityUnavailableCodes.RedisRemediationRef}");
        details.Should().Contain(detail => detail!.StartsWith("remediation: ", StringComparison.Ordinal));
    }
}
