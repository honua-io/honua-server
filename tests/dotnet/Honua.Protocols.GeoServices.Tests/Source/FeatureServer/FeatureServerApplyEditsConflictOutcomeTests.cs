// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerApplyEditsUpdateConflictTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

    private readonly SnapshotPin _pin = new();
    private readonly WebAppFixture _fixture;

    public FeatureServerApplyEditsUpdateConflictTests()
    {
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .DecorateFeatureReaders(inner => new PinnedSnapshotFeatureReader(inner, _pin));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateAgainstStaleSnapshot_ReturnsUpdateConflictAndLeavesTheWinningWriteIntact()
    {
        var objectId = await AddFeatureAsync("conflict-original");
        await PinCurrentSnapshotAsync(objectId);

        // The concurrent writer wins the row while the pinned snapshot still describes the
        // pre-write state, exactly as it would for a client whose read came from a stale replica.
        await MutateStoredNameAsync(objectId, "conflict-winner");

        var response = await PostApplyEditsAsync(UpdateNamePayload(objectId, "conflict-loser"));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);

        result.Success.Should().BeFalse(body);
        result.UpdateResults.Should().ContainSingle(body);
        result.UpdateResults![0].Success.Should().BeFalse(body);
        result.UpdateResults[0].ObjectId.Should().Be(objectId, body);
        result.UpdateResults[0].Error!.Code.Should().Be(
            GeoServicesEditErrorCodes.UpdateConflict,
            "an update whose read snapshot never matches the stored row is the documented " +
            $"update-update conflict, not a generic failure: {body}");

        // The whole point of the conflict code: the loser must not have overwritten the winner.
        (await ReadStoredNameAsync(objectId)).Should().Be(
            "conflict-winner",
            "the losing writer's value must never reach the database");

        _pin.Active = false;
        await AssertQueriedNameAsync(objectId, "conflict-winner");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_AfterUpdateConflict_ClientRereadAndRetrySucceeds()
    {
        var objectId = await AddFeatureAsync("recovery-original");
        await PinCurrentSnapshotAsync(objectId);
        await MutateStoredNameAsync(objectId, "recovery-winner");

        var conflicted = await PostApplyEditsAsync(
            UpdateNamePayload(objectId, "recovery-retry"));
        var conflictedBody = await conflicted.Content.ReadAsStringAsync();
        conflicted.Be200Ok();
        Deserialize(conflictedBody).UpdateResults![0].Error!.Code
            .Should().Be(GeoServicesEditErrorCodes.UpdateConflict, conflictedBody);

        // The documented client contract for this code is "re-read and retry" — so it must be a
        // recoverable outcome, not a row the client can never edit again. Dropping the pin is the
        // client's re-read.
        _pin.Active = false;

        var retry = await PostApplyEditsAsync(UpdateNamePayload(objectId, "recovery-retry"));
        var retryBody = await retry.Content.ReadAsStringAsync();
        retry.Be200Ok();
        var retried = Deserialize(retryBody);
        retried.UpdateResults.Should().ContainSingle(edit => edit.Success && edit.ObjectId == objectId, retryBody);

        (await ReadStoredNameAsync(objectId)).Should().Be(
            "recovery-retry",
            "the retry observed the winner's state and its write must now land");
    }

    private static string UpdateNamePayload(long objectId, string name)
        => """{"updates":[{"attributes":{"objectid":OBJECTID,"name":"NAME"}}]}"""
            .Replace("OBJECTID", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("NAME", name, StringComparison.Ordinal);

    private async Task<long> AddFeatureAsync(string name)
    {
        var response = await PostApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);
        return result.AddResults.Should().ContainSingle(add => add.Success, body).Subject.ObjectId!.Value;
    }

    private async Task PinCurrentSnapshotAsync(long objectId)
    {
        _pin.CaptureObjectId = objectId;
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?f=json&objectIds={objectId}&outFields=*&returnGeometry=true");
        response.Be200Ok();
        _pin.Captured.Should().NotBeNull("the decorated reader must have observed the row to pin it");
        _pin.Active = true;
    }

    private async Task MutateStoredNameAsync(long objectId, string name)
        => (await _fixture.UpdateStoredFeatureNameAsync(LayerId, objectId, name))
            .Should().Be(1, "the interfering write must hit the row");

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(LayerId, objectId);

    private async Task AssertQueriedNameAsync(long objectId, string expected)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?f=json&objectIds={objectId}&outFields=*");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("features").EnumerateArray().Single()
            .GetProperty("attributes").GetProperty("name").GetString().Should().Be(expected);
    }

    private async Task<HttpResponseMessage> PostApplyEditsAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
    }

    private static ApplyEditsResponse Deserialize(string body)
        => JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
           ?? throw new InvalidOperationException($"Expected an apply-edits response: {body}");

    private sealed class SnapshotPin
    {
        public long? CaptureObjectId { get; set; }

        public Feature? Captured { get; set; }

        public bool Active { get; set; }
    }

    private sealed class PinnedSnapshotFeatureReader(IFeatureReader inner, SnapshotPin pin) : IFeatureReader
    {
        public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        {
            if (pin is { Active: true, Captured: { } captured } && featureId == pin.CaptureObjectId)
            {
                return captured;
            }

            var feature = await inner.GetAsync(layerId, featureId, cancellationToken);
            Capture(feature);
            return feature;
        }

        public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            if (pin is { Active: true, Captured: { } captured } &&
                pin.CaptureObjectId is { } pinnedId &&
                query.ObjectIds is { IsDefaultOrEmpty: false } objectIds &&
                objectIds.Contains(pinnedId))
            {
                return new QueryResult<Feature> { Items = [captured], TotalCount = 1 };
            }

            var result = await inner.QueryAsync(layerId, query, cancellationToken);
            foreach (var item in result.Items)
            {
                Capture(item);
            }

            return result;
        }

        private void Capture(Feature? feature)
        {
            if (pin.Captured is null && feature is { } row && row.Id == pin.CaptureObjectId)
            {
                pin.Captured = row;
            }
        }

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryFlatGeobufAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryObjectIdsAsync(layerId, query, cancellationToken);

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.CountAsync(layerId, query, cancellationToken);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => inner.GetExtentAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryStatisticsAsync(layerId, query, cancellationToken);

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
            => inner.GetTemporalExtentAsync(layerId, fieldName, propertyType, cancellationToken);

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => inner.GetEstimatesAsync(layerId, cancellationToken);

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
            int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryTopFeaturesAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
            => inner.QueryDateBinsAsync(layerId, query, dateBin, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
            => inner.QueryBinsAsync(layerId, query, binDefinition, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
            => inner.QueryH3Async(layerId, query, h3Query, cancellationToken);
    }
}

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerApplyEditsFeatureLockedTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

    private readonly LockRegistry _locks = new();
    private readonly WebAppFixture _fixture;

    public FeatureServerApplyEditsFeatureLockedTests()
    {
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .DecorateService<IFeatureWriter>(inner => new LockAwareFeatureWriter(inner, _locks));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateOfLockedFeature_ReturnsFeatureLockedAndLeavesStoredRowIntact()
    {
        var lockedId = await AddFeatureAsync("locked-original");
        var freeId = await AddFeatureAsync("free-original");
        _locks.Locked.Add(lockedId);

        // rollbackOnFailure=false so the unlocked sibling still commits: the lock must fail exactly
        // one operation, not the batch.
        var response = await PostApplyEditsAsync(
            $$$"""
            {"updates":[{"attributes":{"objectid":{{{lockedId}}},"name":"locked-attempt"}},
                        {"attributes":{"objectid":{{{freeId}}},"name":"free-updated"}}],
             "rollbackOnFailure":false}
            """);
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);

        result.UpdateResults.Should().HaveCount(2, body);
        var locked = result.UpdateResults!.Single(edit => edit.ObjectId == lockedId);
        locked.Success.Should().BeFalse(body);
        locked.Error!.Code.Should().Be(
            GeoServicesEditErrorCodes.FeatureLocked,
            $"a writer reporting HTTP 423 semantics is the documented lock class: {body}");

        result.UpdateResults!.Single(edit => edit.ObjectId == freeId).Success.Should().BeTrue(body);

        (await ReadStoredNameAsync(lockedId)).Should().Be("locked-original", "a locked feature must not be modified");
        (await ReadStoredNameAsync(freeId)).Should().Be("free-updated", "the unlocked sibling still commits");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_DeleteOfLockedFeature_ReturnsFeatureLockedAndLeavesStoredRowIntact()
    {
        var lockedId = await AddFeatureAsync("locked-delete-original");
        _locks.Locked.Add(lockedId);

        var response = await PostApplyEditsAsync($$$"""{"deletes":[{{{lockedId}}}],"rollbackOnFailure":false}""");
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);

        result.DeleteResults.Should().ContainSingle(body);
        result.DeleteResults![0].Success.Should().BeFalse(body);
        result.DeleteResults[0].Error!.Code.Should().Be(
            GeoServicesEditErrorCodes.FeatureLocked,
            "the lock class is reported for deletes too, ahead of the delete-delete class: " + body);

        (await ReadStoredNameAsync(lockedId)).Should().Be(
            "locked-delete-original",
            "a locked feature must survive the rejected delete");
    }

    private async Task<long> AddFeatureAsync(string name)
    {
        var response = await PostApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        return Deserialize(body).AddResults.Should().ContainSingle(add => add.Success, body).Subject.ObjectId!.Value;
    }

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(LayerId, objectId);

    private async Task<HttpResponseMessage> PostApplyEditsAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
    }

    private static ApplyEditsResponse Deserialize(string body)
        => JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
           ?? throw new InvalidOperationException($"Expected an apply-edits response: {body}");

    private sealed class LockRegistry
    {
        public HashSet<long> Locked { get; } = [];
    }

    private sealed class LockAwareFeatureWriter(IFeatureWriter inner, LockRegistry locks) : IFeatureWriter
    {
        private const int LockedErrorCode = 423;
        private const string LockedMessage = "The feature is locked by another editor.";

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.CreateAsync(layerId, feature, cancellationToken);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(layerId, feature, cancellationToken);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(layerId, featureId, cancellationToken);

        public async Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            var lockedUpdates = editBatch.Updates.Where(feature => locks.Locked.Contains(feature.Id)).ToArray();
            var lockedDeletes = editBatch.Deletes.Where(locks.Locked.Contains).ToArray();
            if (lockedUpdates.Length == 0 && lockedDeletes.Length == 0)
            {
                return await inner.ApplyEditsAsync(layerId, editBatch, cancellationToken);
            }

            var forwarded = editBatch with
            {
                Updates = editBatch.Updates.Where(feature => !locks.Locked.Contains(feature.Id)).ToImmutableArray(),
                Deletes = editBatch.Deletes.Where(objectId => !locks.Locked.Contains(objectId)).ToImmutableArray()
            };

            var inner_result = forwarded.Updates.IsEmpty && forwarded.Deletes.IsEmpty && forwarded.Creates.IsEmpty
                ? FeatureEditResult.Success(0, 0, 0)
                : await inner.ApplyEditsAsync(layerId, forwarded, cancellationToken);

            // Re-assemble per-operation results in the batch's own order so each result still lines
            // up with the request slot the handler dispatched.
            var updateResults = Merge(
                editBatch.Updates.Select(feature => feature.Id),
                inner_result.UpdateResults);
            var deleteResults = Merge(editBatch.Deletes, inner_result.DeleteResults);

            return FeatureEditResult.Success(
                inner_result.CreatedCount,
                inner_result.UpdatedCount,
                inner_result.DeletedCount,
                createdIds: inner_result.CreatedIds,
                createResults: inner_result.CreateResults,
                updateResults: updateResults,
                deleteResults: deleteResults);
        }

        private ImmutableArray<EditOperationResult> Merge(
            IEnumerable<long> requestedObjectIds,
            ImmutableArray<EditOperationResult> forwardedResults)
        {
            var forwarded = new Queue<EditOperationResult>(forwardedResults);
            var merged = ImmutableArray.CreateBuilder<EditOperationResult>();
            foreach (var objectId in requestedObjectIds)
            {
                merged.Add(locks.Locked.Contains(objectId)
                    ? EditOperationResult.Failure(LockedMessage, LockedErrorCode, objectId)
                    : forwarded.Dequeue());
            }

            return merged.ToImmutable();
        }
    }
}

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerApplyEditsConflictCodePostgresTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;
    private const long MissingObjectId = 999_999;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateMissingFeatureOnPostgres_ReturnsNotFoundAndWritesNothing()
    {
        var bystanderId = await AddFeatureAsync("bystander");
        var baseline = await ReadRowCountAsync();

        var response = await PostApplyEditsAsync(
            $$$"""{"updates":[{"attributes":{"objectid":{{{MissingObjectId}}},"name":"ghost"}}],"rollbackOnFailure":false}""");
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);

        result.Success.Should().BeFalse(body);
        result.UpdateResults.Should().ContainSingle(body);
        result.UpdateResults![0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.NotFound, body);

        (await ReadRowCountAsync()).Should().Be(baseline, "a not-found update must not insert a row");
        (await ReadStoredNameAsync(bystanderId)).Should().Be("bystander");
        (await ReadStoredNameAsync(MissingObjectId)).Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_DeleteMissingFeatureOnPostgres_ReturnsDeleteNotFoundAndDeletesNothing()
    {
        var bystanderId = await AddFeatureAsync("delete-bystander");
        var baseline = await ReadRowCountAsync();

        var response = await PostApplyEditsAsync(
            $$$"""{"deletes":[{{{MissingObjectId}}}],"rollbackOnFailure":false}""");
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);

        result.Success.Should().BeFalse(body);
        result.DeleteResults.Should().ContainSingle(body);
        result.DeleteResults![0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.DeleteNotFound, body);

        (await ReadRowCountAsync()).Should().Be(baseline, "a delete-delete conflict must not remove another row");
        (await ReadStoredNameAsync(bystanderId)).Should().Be("delete-bystander");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_DeleteNonNumericObjectIdOnPostgres_ReturnsInvalidObjectIdAndDeletesNothing()
    {
        var bystanderId = await AddFeatureAsync("invalid-id-bystander");
        var baseline = await ReadRowCountAsync();

        var response = await PostApplyEditsAsync(
            """{"deletes":["not-a-number"],"rollbackOnFailure":false}""");
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        var result = Deserialize(body);

        result.Success.Should().BeFalse(body);
        result.DeleteResults.Should().ContainSingle(body);
        result.DeleteResults![0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.InvalidObjectId, body);

        (await ReadRowCountAsync()).Should().Be(baseline, "an unparseable object id must not delete anything");
        (await ReadStoredNameAsync(bystanderId)).Should().Be("invalid-id-bystander");
    }

    private async Task<long> AddFeatureAsync(string name)
    {
        var response = await PostApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        return Deserialize(body).AddResults.Should().ContainSingle(add => add.Success, body).Subject.ObjectId!.Value;
    }

    private Task<long> ReadRowCountAsync() => _fixture.CountStoredFeaturesAsync(LayerId);

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(LayerId, objectId);

    private async Task<HttpResponseMessage> PostApplyEditsAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
    }

    private static ApplyEditsResponse Deserialize(string body)
        => JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
           ?? throw new InvalidOperationException($"Expected an apply-edits response: {body}");
}
