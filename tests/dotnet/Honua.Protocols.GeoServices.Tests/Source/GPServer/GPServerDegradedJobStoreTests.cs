// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

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
        details.Should().Contain($"missingDependency: {CapabilityUnavailableCodes.SystemDatabaseDependency}");
        details.Should().Contain($"capability: {CapabilityUnavailableCodes.DurableJobsCapability}");
        details.Should().Contain($"remediationRef: {CapabilityUnavailableCodes.SystemDatabaseRemediationRef}");
        details.Should().Contain(detail => detail!.StartsWith("remediation: ", StringComparison.Ordinal));
    }
}
