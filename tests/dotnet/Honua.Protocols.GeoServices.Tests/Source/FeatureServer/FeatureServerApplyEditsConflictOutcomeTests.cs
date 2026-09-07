// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Produces the two conflict classes that the pure <c>ClassifyWriterFailure_*</c> unit tests in
/// <see cref="FeatureServerApplyEditsConflictCodeTests"/> only assert in isolation —
/// <see cref="GeoServicesEditErrorCodes.UpdateConflict"/> (update-update) and
/// <see cref="GeoServicesEditErrorCodes.FeatureLocked"/> — through a real HTTP <c>applyEdits</c>
/// against real PostGIS, and asserts the stored row afterwards so a losing writer is proven not to
/// have overwritten the winner (honua-server#4406).
/// </summary>
/// <remarks>
/// <para>
/// <b>How the update-update conflict is produced deterministically.</b> A wire <c>applyEdits</c>
/// update is assembled from a read snapshot; the handler carries that snapshot's
/// <see cref="FeatureStateToken"/> into the write transaction as a precondition, and the Postgres
/// writer re-computes the token from the row it locked with <c>SELECT … FOR UPDATE</c>
/// (<c>FeatureDataAccess.Edits.cs::EnsurePreconditionSatisfiedAsync</c>). When they differ the row
/// changed under the request and the operation fails with
/// <see cref="EditOperationResult.PreconditionFailed"/>. For an ordinary single-row update the
/// handler then re-reads, re-merges and retries up to 16 times, so a <em>single</em> interfering
/// write is invisible to the client — that recovery is what
/// <c>FeatureServerMutationScenarioTests.ApplyEdits_ConcurrentDistinctPartialUpdates_RereadAfterLockedSnapshotChanges</c>
/// proves. The conflict code only reaches the client when the snapshot never catches up, which is
/// what a client reading a stale replica sees (honua-server#4259).
/// </para>
/// <para>
/// Reproducing that by racing 16 retries would be timing-dependent, so this fixture pins the read
/// snapshot instead: <see cref="PinnedSnapshotFeatureReader"/> decorates the <b>real</b> Postgres
/// <see cref="IFeatureReader"/> and replays a genuine earlier row for one object id. Every other
/// call still reaches Postgres, and the write path — the row, the <c>FOR UPDATE</c> lock, the token
/// comparison inside the write transaction, the retry budget, the HTTP envelope — is entirely real.
/// The stale value is real data from a real read, taken before a real concurrent write committed.
/// </para>
/// </remarks>
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
            .DecorateService<IFeatureReader>(inner => new PinnedSnapshotFeatureReader(inner, _pin));
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

    /// <summary>
    /// Drives one ordinary read through the decorated reader so it captures the genuine current
    /// row, then arms replay of that captured snapshot.
    /// </summary>
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

    /// <summary>Reads the stored value straight out of Postgres, bypassing the pinned reader.</summary>
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

    /// <summary>
    /// Shared across the scoped reader instances the DI container creates per request, so the test
    /// can capture a row once and then replay it to every subsequent read.
    /// </summary>
    private sealed class SnapshotPin
    {
        public long? CaptureObjectId { get; set; }

        public Feature? Captured { get; set; }

        public bool Active { get; set; }
    }

    /// <summary>
    /// Decorates the real feature reader and, once armed, replays a previously captured row for one
    /// object id. Everything else — every other layer, every other object id, every other query
    /// shape — passes through to Postgres untouched.
    /// </summary>
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

/// <summary>
/// <see cref="GeoServicesEditErrorCodes.FeatureLocked"/> is a published contract code for
/// lock-aware providers; the shipped Postgres writer has no lock manager, so no default write path
/// can produce it (see the remarks on the constant). This fixture therefore supplies the missing
/// half — a lock-aware writer, decorating the <b>real</b> Postgres writer — and proves the whole
/// path a lock-aware provider depends on: the 423 the provider reports becomes error code 1005 in
/// the <c>applyEdits</c> HTTP envelope, the operation's object id is preserved, and the stored
/// PostGIS row is untouched (honua-server#4406).
/// </summary>
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

    /// <summary>
    /// Stands in for a lock-aware provider: operations targeting a held feature are refused with
    /// the writer contract's HTTP-423 error code and never dispatched, while every other operation
    /// — and the whole rest of the batch — reaches the real Postgres writer.
    /// </summary>
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

/// <summary>
/// Real-PostGIS variants of the three handler-determined conflict classes that
/// <see cref="FeatureServerApplyEditsConflictCodeTests"/> only proves against the in-memory
/// <c>TestFeatureStore</c>. Each case additionally reads the database back, so a code that was
/// returned while the edit nevertheless landed (or while an unrelated row was destroyed) fails
/// here (honua-server#4406).
/// </summary>
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
