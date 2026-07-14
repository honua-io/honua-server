// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Conformance tests for the stable per-feature conflict classification codes returned in the
/// <c>applyEdits</c> HTTP-200 result envelope (#2251). Two layers of coverage:
/// <list type="bullet">
/// <item>Pure tests of <see cref="GeoServicesEditErrorCodes.ClassifyWriterFailure"/>, which maps a
/// writer's typed <see cref="EditOperationResult"/> onto a stable code (update-update, locked,
/// not-found/delete-delete, generic) without inspecting the free-form message.</item>
/// <item>HTTP tests asserting the handler-determined classes (not-found, delete-delete, invalid
/// object id) surface the documented code on the real <c>applyEdits</c> endpoint.</item>
/// </list>
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerApplyEditsConflictCodeTests
{
    // --- Pure classifier coverage (one assertion per conflict class) -----------------------------

    [Fact]
    public void ClassifyWriterFailure_PreconditionFailure_IsUpdateConflict()
    {
        var result = EditOperationResult.PreconditionFailed(objectId: 7);

        GeoServicesEditErrorCodes.ClassifyWriterFailure(result, FeatureEditOperationKind.Update)
            .Should().Be(GeoServicesEditErrorCodes.UpdateConflict);
    }

    [Fact]
    public void ClassifyWriterFailure_PreconditionErrorCodeWithoutFlag_IsUpdateConflict()
    {
        // A writer that reports the well-known precondition code numerically (without the typed
        // flag) is still classified as an update-update conflict.
        var result = EditOperationResult.Failure(
            "conflict",
            errorCode: EditOperationResult.PreconditionFailedErrorCode,
            objectId: 7);

        GeoServicesEditErrorCodes.ClassifyWriterFailure(result, FeatureEditOperationKind.Update)
            .Should().Be(GeoServicesEditErrorCodes.UpdateConflict);
    }

    [Fact]
    public void ClassifyWriterFailure_LockCode_IsFeatureLocked()
    {
        var result = EditOperationResult.Failure("locked", errorCode: 423, objectId: 7);

        GeoServicesEditErrorCodes.ClassifyWriterFailure(result, FeatureEditOperationKind.Update)
            .Should().Be(GeoServicesEditErrorCodes.FeatureLocked);
    }

    [Fact]
    public void ClassifyWriterFailure_NotFoundCode_MapsByOperationKind()
    {
        var result = EditOperationResult.Failure("gone", errorCode: 404, objectId: 7);

        GeoServicesEditErrorCodes.ClassifyWriterFailure(result, FeatureEditOperationKind.Update)
            .Should().Be(GeoServicesEditErrorCodes.NotFound);
        GeoServicesEditErrorCodes.ClassifyWriterFailure(result, FeatureEditOperationKind.Delete)
            .Should().Be(GeoServicesEditErrorCodes.DeleteNotFound);
    }

    [Fact]
    public void ClassifyWriterFailure_UnknownCode_IsGenericFallback()
    {
        var result = EditOperationResult.Failure("boom", objectId: 7);

        GeoServicesEditErrorCodes.ClassifyWriterFailure(result, FeatureEditOperationKind.Create)
            .Should().Be(GeoServicesEditErrorCodes.GenericFailure);
    }

    [Fact]
    public void StableCodes_AreDistinct()
    {
        int[] codes =
        [
            GeoServicesEditErrorCodes.GenericFailure,
            GeoServicesEditErrorCodes.InvalidObjectId,
            GeoServicesEditErrorCodes.NotFound,
            GeoServicesEditErrorCodes.DeleteNotFound,
            GeoServicesEditErrorCodes.UpdateConflict,
            GeoServicesEditErrorCodes.FeatureLocked,
            GeoServicesEditErrorCodes.ValidationFailed,
            GeoServicesEditErrorCodes.NotPermitted,
            GeoServicesEditErrorCodes.OperationRolledBack,
        ];

        codes.Should().OnlyHaveUniqueItems();
    }

    // --- HTTP envelope coverage for handler-determined classes -----------------------------------

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateMissingFeature_ReturnsNotFoundCode()
    {
        await using var harness = await EditHarness.CreateAsync();

        var response = await harness.PostApplyEditsAsync(new ApplyEditsRequest
        {
            Updates =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?> { ["objectid"] = 999_999, ["name"] = "ghost" }
                }
            ]
        });

        response.Be200Ok();
        var result = await harness.ReadAsync(response);
        result.Success.Should().BeFalse();
        result.UpdateResults.Should().ContainSingle();
        result.UpdateResults![0].Success.Should().BeFalse();
        result.UpdateResults[0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_DeleteMissingFeature_ReturnsDeleteConflictCode()
    {
        await using var harness = await EditHarness.CreateAsync();

        var response = await harness.PostApplyEditsAsync(new ApplyEditsRequest
        {
            Deletes = [999_999]
        });

        response.Be200Ok();
        var result = await harness.ReadAsync(response);
        result.Success.Should().BeFalse();
        result.DeleteResults.Should().ContainSingle();
        result.DeleteResults![0].Success.Should().BeFalse();
        result.DeleteResults[0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.DeleteNotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_DeleteNonNumericObjectId_ReturnsInvalidObjectIdCode()
    {
        await using var harness = await EditHarness.CreateAsync();

        var response = await harness.PostApplyEditsAsync(new ApplyEditsRequest
        {
            Deletes = ["not-a-number"]
        });

        response.Be200Ok();
        var result = await harness.ReadAsync(response);
        result.Success.Should().BeFalse();
        result.DeleteResults.Should().ContainSingle();
        result.DeleteResults![0].Success.Should().BeFalse();
        result.DeleteResults[0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.InvalidObjectId);
    }

    /// <summary>
    /// Spins up the FeatureServer edit endpoint backed by an in-memory <see cref="TestFeatureStore"/>
    /// under a Pro license (the FeatureServer editing gate, #1591). Mirrors the harness used by the
    /// plugin-validation conformance tests.
    /// </summary>
    private sealed class EditHarness : IAsyncDisposable
    {
        private const string ServiceId = "test";
        private const int LayerId = 0;

        private readonly WebAppFixture _fixture;

        private EditHarness(WebAppFixture fixture) => _fixture = fixture;

        public static async Task<EditHarness> CreateAsync()
        {
            var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
            var store = new TestFeatureStore();
            fixture.ReplaceService<IFeatureReader>(store);
            fixture.ReplaceService<IFeatureWriter>(store);
            fixture.ReplaceService<ITileProvider>(store);
            fixture.ReplaceService<IRelationshipStore>(store);
            fixture.ReplaceService<IStreamingFeatureStore>(store);
            await fixture.InitializeAsync();
            return new EditHarness(fixture);
        }

        public async Task<HttpResponseMessage> PostApplyEditsAsync(ApplyEditsRequest request)
        {
            var json = JsonSerializer.Serialize(request, FeatureServerJsonContext.Default.ApplyEditsRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await _fixture.Client.PostAsync(
                $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
        }

        public async Task<ApplyEditsResponse> ReadAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse);
            parsed.Should().NotBeNull();
            return parsed!;
        }

        public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    }
}
