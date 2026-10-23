// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Regression coverage for honua-server#4640: the flaky trunk red where
/// <c>ClientCompatSeed_OnServerMigratedDatabase_...</c> intermittently 500'd instead of
/// returning the graceful 402 refusal on an unlicensed host's <c>addFeatures</c>/<c>applyEdits</c>
/// call. Root cause — <c>FeatureServerEditsHandler</c>'s constructor pulled in
/// <see cref="IResourceValidator"/> (and, through it, <c>IMetadataV2GraphProvider</c>) via
/// minimal-API <c>[FromServices]</c> parameter binding, which resolved unconditionally before the
/// entitlement gate in the method body ever ran — so a transient DI hiccup in that graph could
/// leak a raw 500 through the graceful GeoServices refusal, on a request that was always going to
/// be denied regardless.
/// </summary>
/// <remarks>
/// Rather than trying to reproduce the transient race itself (the original diagnosis could not
/// reproduce it locally in 9/9 runs — see #4640), this proves the fix's actual mechanism
/// deterministically: <see cref="IResourceValidator"/> is replaced with an implementation that
/// throws unconditionally, standing in for "this dependency is broken right now for any reason."
/// A license-denied FeatureServer edit must still return the graceful 402 body — proving the
/// entitlement gate runs, and the write pipeline resolves nothing that touches
/// <see cref="IResourceValidator"/>, before the request is refused.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.ApplyEdits)]
public sealed class FeatureEditsDeniedPathSkipsResourceValidatorTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ReplaceService<IResourceValidator>(new ThrowingResourceValidator());

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    public async Task AddFeatures_UnlicensedHostWithBrokenResourceValidator_Returns402NotA500()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["features"] = """[{"attributes":{"name":"regression-4640"}}]"""
        });

        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/addFeatures", content);
        var body = await response.Content.ReadAsStringAsync();

        // GeoServices signals a refused operation in the body, never in the status (PA-070/PA-117).
        // If the entitlement gate ran after IResourceValidator was touched, ThrowingResourceValidator
        // would have surfaced its exception as an unhandled raw 500 here instead.
        response.Be200Ok();

        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(
            402,
            "an unlicensed deployment must refuse FeatureServer editing through the entitlement gate " +
            "before ever touching IResourceValidator, even when that dependency is broken (#4640)");
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UnlicensedHostWithBrokenResourceValidator_Returns402NotA500()
    {
        using var content = new StringContent(
            """{"adds":[{"attributes":{"name":"regression-4640"}}]}""", Encoding.UTF8, "application/json");

        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
        var body = await response.Content.ReadAsStringAsync();

        response.Be200Ok();

        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(
            402,
            "applyEdits must be refused the same way addFeatures is, before touching IResourceValidator");
    }

    /// <summary>
    /// Stands in for "this dependency is broken right now for any reason" (a transient DI
    /// resolution failure, a downstream construction error, etc.). Every member throws
    /// unconditionally so the test fails loudly if the write pipeline ever calls it on a
    /// license-denied request.
    /// </summary>
    private sealed class ThrowingResourceValidator : IResourceValidator
    {
        private static InvalidOperationException Poisoned() =>
            new("IResourceValidator must not be resolved or called for a license-denied FeatureServer edit (#4640).");

        public Task<ResourceValidationResult<MetadataV2Resource>> ValidateLayerV2Async(
            int layerId, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2Resource>> ValidateCollectionV2Async(
            string collectionId, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
            string serviceId, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
            string serviceId, string requiredProtocol, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
            string serviceId, int layerId, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
            string serviceId, int layerId, string requiredProtocol, CancellationToken cancellationToken = default) => throw Poisoned();
    }
}
