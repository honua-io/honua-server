// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// At-most-once <c>applyEdits</c> proven by counting rows in PostGIS rather than by inspecting the
/// response envelope (honua-server#4406). The existing <c>Idempotency-Key</c> suites
/// (<see cref="FeatureServerApplyEditsIdempotencyReleaseTests"/> and its siblings) assert the error
/// code a retry receives; a duplicate insert and a correctly deduplicated retry can produce the
/// same body, so only a <c>SELECT count(*)</c> after the retry distinguishes them. Every test here
/// runs the real Postgres writer.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerApplyEditsIdempotentReplayTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

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
    public async Task ApplyEdits_RetryWithSameIdempotencyKey_ReplaysTheResponseAndLeavesExactlyOneRow()
    {
        var key = Guid.NewGuid().ToString("n");
        var name = $"replay-{key}";
        var payload = AddPayload(name);

        var first = await PostAsync(payload, key);
        var firstBody = await first.Content.ReadAsStringAsync();
        first.Be200Ok();
        var created = Deserialize(firstBody);
        created.Success.Should().BeTrue(firstBody);
        var objectId = created.AddResults.Should().ContainSingle(add => add.Success, firstBody).Subject.ObjectId!.Value;
        (await CountAsync(name)).Should().Be(1, "the first request committed exactly one row");

        var retry = await PostAsync(payload, key);
        var retryBody = await retry.Content.ReadAsStringAsync();
        retry.Be200Ok();
        var replayed = Deserialize(retryBody);

        replayed.AddResults.Should().ContainSingle(retryBody);
        replayed.AddResults![0].ObjectId.Should().Be(
            objectId,
            $"the replay must return the original object id, not a newly allocated one: {retryBody}");

        (await CountAsync(name)).Should().Be(
            1,
            "the whole point of the Idempotency-Key is that the retry creates no second row — a " +
            "duplicated insert and a deduplicated retry return the same 200 body, so only the row " +
            "count can tell them apart");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_ResubmitWithADifferentIdempotencyKey_CreatesASecondRow()
    {
        // Control for the test above: without this, a schema-level unique constraint (or any other
        // incidental de-duplication) would make the row-count assertion pass for the wrong reason.
        var name = $"distinct-keys-{Guid.NewGuid():n}";
        var payload = AddPayload(name);

        (await PostAsync(payload, Guid.NewGuid().ToString("n"))).Be200Ok();
        (await CountAsync(name)).Should().Be(1);

        (await PostAsync(payload, Guid.NewGuid().ToString("n"))).Be200Ok();

        (await CountAsync(name)).Should().Be(
            2,
            "an identical payload under a different key is a different edit and must be applied");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_EightConcurrentRequestsWithTheSameKey_CommitExactlyOneRow()
    {
        // The reservation exists so that concurrent duplicates — a client retrying before the first
        // response arrives, or a load balancer replaying a request — cannot both execute. The
        // losers are answered with the idempotency conflict (409) or with the winner's replayed
        // response; either way the database must hold exactly one row.
        var key = Guid.NewGuid().ToString("n");
        var name = $"concurrent-{key}";
        var payload = AddPayload(name);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostAsync(payload, key)));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));

        foreach (var response in responses)
        {
            response.Be200Ok();
        }

        var applied = bodies.Count(body => Deserialize(body).Success);
        applied.Should().BeGreaterThan(0, "one concurrent request must win the reservation and apply the edit");

        (await CountAsync(name)).Should().Be(
            1,
            "at-most-once means at most one row, however many duplicates arrive concurrently: " +
            string.Join(" | ", bodies));
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_RetryOfADeleteWithTheSameKey_DoesNotRemoveAReusedObjectId()
    {
        var key = Guid.NewGuid().ToString("n");
        var name = $"delete-replay-{key}";

        var addBody = await ReadAsync(await PostAsync(AddPayload(name), Guid.NewGuid().ToString("n")));
        var objectId = Deserialize(addBody).AddResults!.Single().ObjectId!.Value;

        var deletePayload = """{"deletes":[OBJECTID]}"""
            .Replace("OBJECTID", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var firstDelete = Deserialize(await ReadAsync(await PostAsync(deletePayload, key)));
        firstDelete.DeleteResults.Should().ContainSingle(result => result.Success && result.ObjectId == objectId);
        (await CountAsync(name)).Should().Be(0);

        // Re-create a row under the SAME object id the delete targeted, then replay the delete.
        // objectid is BIGSERIAL, so an ordinary add would take the next sequence value and the
        // replayed delete would harmlessly target an absent row — the assertion would then pass
        // even with replay protection broken. Reinsert the exact id instead.
        var revivedName = $"{name}-revived";
        await ReinsertWithObjectIdAsync(objectId, revivedName);
        (await CountAsync(revivedName)).Should().Be(1, "the row was re-created under the deleted object id");

        var replay = Deserialize(await ReadAsync(await PostAsync(deletePayload, key)));
        replay.DeleteResults.Should().ContainSingle().Which.ObjectId.Should().Be(objectId);

        (await CountAsync(revivedName)).Should().Be(
            1,
            $"the replayed delete must be answered from the recorded response, not re-executed — " +
            $"re-executing it would delete the row now occupying object id {objectId}");
    }

    /// <summary>
    /// Re-creates a row under an explicit object id, so a replayed delete has something to destroy
    /// if replay protection fails.
    /// </summary>
    private async Task ReinsertWithObjectIdAsync(long objectId, string name)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, objectid, geometry, attributes)
            VALUES (@layerId, @objectId, ST_SetSRID(ST_Point(-122.4194, 37.7749), 4326),
                    jsonb_build_object('name', @name));
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("objectId", objectId);
        command.Parameters.AddWithValue("name", name);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static string AddPayload(string name)
        => """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
            .Replace("NAME", name, StringComparison.Ordinal);

    private Task<long> CountAsync(string name) => _fixture.CountStoredFeaturesByNameAsync(LayerId, name);

    private static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        return body;
    }

    private async Task<HttpResponseMessage> PostAsync(string json, string idempotencyKey)
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

    private static ApplyEditsResponse Deserialize(string body)
        => JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
           ?? throw new InvalidOperationException($"Expected an apply-edits response: {body}");
}
