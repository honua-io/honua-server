// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    private sealed class ThrowingResourceValidator : IResourceValidator
    {
        private static InvalidOperationException Poisoned() =>
            new("IResourceValidator must not be resolved or called for a license-denied FeatureServer edit (#4640).");

        public Task<ResourceValidationResult<MetadataV2Object>> ValidateStorageLayerV2Async(
            int storageLayerId, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2Object>> ValidateLayerV2Async(
            int layerId, CancellationToken cancellationToken = default) => throw Poisoned();

        public Task<ResourceValidationResult<MetadataV2Object>> ValidateCollectionV2Async(
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
