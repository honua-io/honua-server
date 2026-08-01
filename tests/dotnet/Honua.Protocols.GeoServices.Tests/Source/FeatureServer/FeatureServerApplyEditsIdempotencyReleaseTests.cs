// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Regression coverage for honua-server#3052: the <c>Idempotency-Key</c> reservation the shared
/// FeatureServer edit pipeline takes before executing an edit must be released on the exception
/// exits, not only replaced on the success exit. This class covers the exception boundary — the
/// provider rejection raised as <see cref="NotSupportedException"/> after the reservation is held —
/// on the ordinary Postgres host, so the guarantee is proven in the primary integration lane rather
/// than only in the read-only provider smoke lane.
/// </summary>
/// <remarks>
/// The host is the standard fixture with one substitution: <see cref="IFeatureWriter"/> is the
/// production <see cref="ReadOnlyFeatureWriter"/> that DuckDB and MySQL/MariaDB register, which
/// rejects every write with <see cref="NotSupportedException"/>. Reads, metadata, authorization and
/// the idempotency store are untouched, so the request under test travels the real edit pipeline
/// and fails exactly where a read/query-only provider fails it.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerApplyEditsIdempotencyReleaseTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IFeatureWriter>(new ReadOnlyFeatureWriter("TestReadOnly"));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_RetryAfterProviderRejectionWithSameIdempotencyKey_RepeatsRejectionInsteadOfConflict()
    {
        // #3052: the rejection is thrown after TryReserveAsync has won the key. Before the fix no
        // exception exit released it, so a retry inside the ~60s reservation window found no replay
        // value, lost the reserve, and was answered with the idempotency conflict (error code 409)
        // instead of a deterministic repeat of the original 405 rejection.
        var idempotencyKey = Guid.NewGuid().ToString("n");
        var request = new ApplyEditsRequest
        {
            Adds =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?> { ["name"] = $"Rejected-{idempotencyKey}" },
                    Geometry = new GeoServicesGeometry { X = -122.4194, Y = 37.7749 }
                }
            ]
        };

        var json = JsonSerializer.Serialize(request, FeatureServerJsonContext.Default.ApplyEditsRequest);

        var first = await PostApplyEditsAsync(json, idempotencyKey);
        first.Be200Ok();
        var firstBody = await first.Content.ReadAsStringAsync();
        ReadErrorCode(firstBody).Should().Be(
            405,
            "a read-only provider's write rejection maps to the documented 405, Esri-style (HTTP 200 " +
            $"with an error envelope): {firstBody}");

        var retry = await PostApplyEditsAsync(json, idempotencyKey);
        retry.Be200Ok();
        var retryBody = await retry.Content.ReadAsStringAsync();
        var retryCode = ReadErrorCode(retryBody);
        retryCode.Should().NotBe(
            409,
            "the failed edit released its reservation, so a retry is a fresh attempt rather than a " +
            $"concurrent request: {retryBody}");
        retryCode.Should().Be(405, retryBody);
    }

    private async Task<HttpResponseMessage> PostApplyEditsAsync(string json, string idempotencyKey)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _fixture.Client.SendAsync(message);
    }

    /// <summary>
    /// Reads the GeoServices error code from an Esri-shaped error envelope, or <c>-1</c> when the
    /// response is not an error at all (which the assertions report as a failure).
    /// </summary>
    private static int ReadErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var error)
               && error.TryGetProperty("code", out var code)
            ? code.GetInt32()
            : -1;
    }
}
