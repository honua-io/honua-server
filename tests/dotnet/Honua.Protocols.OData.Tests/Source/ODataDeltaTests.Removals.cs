// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed partial class ODataDeltaTests
{
    /// <summary>
    /// #3872: a delete and a filter exit that carry the same database timestamp as the
    /// baseline they follow must cross the delta token boundary exactly once, in the
    /// documented identity-only removal shape the public SDK consumes, and a change
    /// committed while the subscriber is still paging must not be lost.
    /// </summary>
    /// <remarks>
    /// This complements <c>Delta_EqualTimestampsDeletesRecreateAndFilterTransitions_PageOneConverges</c>,
    /// which proves that the materialized state converges. Convergence alone does not
    /// prove the three properties asserted here, because a materialized view reaches the
    /// same state whether a removal is delivered once, delivered twice, or delivered with
    /// extra fields attached:
    /// <list type="bullet">
    /// <item>the removal payload is identity-only — no attribute or geometry value of a row
    /// that left the query is disclosed through the tombstone;</item>
    /// <item>replaying the terminal delta token yields an empty change set, so a removal is
    /// never re-emitted and a subscriber cannot accumulate phantom work;</item>
    /// <item>a delete committed while the subscriber is mid-paging is absent from the frozen
    /// page image and delivered exactly once by the next poll, so nothing is dropped in the
    /// window between a nextLink and its deltaLink.</item>
    /// </list>
    /// Every mutation here keeps the seeded <c>updated_at</c> value unchanged, so no part of
    /// the proof can be satisfied by a timestamp advancing.
    /// </remarks>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.ODataV4, "DeltaTracking")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Delta_EqualTimestampRemovalsAcrossTokenBoundary_AreIdentityOnlyAndDeliveredExactlyOnce()
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        using var commandBuilder = new NpgsqlCommandBuilder();
        var schema = commandBuilder.QuoteIdentifier(_fixture.CurrentSchema!);

        // One frozen timestamp for the baseline and for every later mutation: the proof
        // must not be satisfiable by a change to updated_at.
        const string FrozenTimestamp = "2026-03-01";
        await ExecuteAsync(connection, $$"""
            DELETE FROM {{schema}}.features WHERE layer_id = 0;
            INSERT INTO {{schema}}.features(objectid, layer_id, geometry, attributes, updated_at)
            VALUES
                (74001, 0, ST_SetSRID(ST_Point(3, 5), 4326), '{"name":"kept"}',        '{{FrozenTimestamp}}'),
                (74002, 0, ST_SetSRID(ST_Point(4, 6), 4326), '{"name":"deleted"}',     '{{FrozenTimestamp}}'),
                (74003, 0, ST_SetSRID(ST_Point(5, 7), 4326), '{"name":"filter-exit"}', '{{FrozenTimestamp}}'),
                (74004, 0, ST_SetSRID(ST_Point(6, 8), 4326), '{"name":"paging"}',      '{{FrozenTimestamp}}'),
                (74005, 0, ST_SetSRID(ST_Point(7, 9), 4326), '{"name":"mid-page"}',    '{{FrozenTimestamp}}');
            """);

        const string TrackedQuery = "/odata/Features(0)?$filter=name%20ne%20'excluded'&$top=1";
        var baseline = await CollectDeltaAsync(TrackedQuery, tracked: true);
        baseline.Changes.Select(ObjectIdOf).Should().BeEquivalentTo(
            new long[] { 74001, 74002, 74003, 74004, 74005 },
            "the tracked baseline is the complete query image");

        // Delete one row, take another out of the query through the filter, and update a
        // third — all at the baseline's own timestamp.
        await ExecuteAsync(connection, $$"""
            DELETE FROM {{schema}}.features WHERE objectid = 74002;
            UPDATE {{schema}}.features SET attributes = '{"name":"excluded"}', updated_at = '{{FrozenTimestamp}}' WHERE objectid = 74003;
            UPDATE {{schema}}.features SET attributes = '{"name":"kept-updated"}', updated_at = '{{FrozenTimestamp}}' WHERE objectid = 74001;
            """);

        var afterRemovals = await CollectDeltaAsync(baseline.DeltaLink!, tracked: false);
        afterRemovals.Changes.Select(ObjectIdOf).Should().BeEquivalentTo(
            new long[] { 74001, 74002, 74003 },
            "an equal-timestamp update, physical delete and filter exit all cross the token boundary");

        var removals = afterRemovals.Changes.Where(IsRemoval).ToArray();
        removals.Select(ObjectIdOf).Should().BeEquivalentTo(
            new long[] { 74002, 74003 },
            "a physical delete and a filter exit are both represented as removals");
        foreach (var removal in removals)
        {
            // The documented tombstone shape: the identity keys the SDK needs to drop the
            // row from its materialized view, and nothing else. A value of a row that left
            // the query must not be disclosed through its tombstone.
            removal.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo(["ObjectId", "LayerId", "@removed"]);
            removal.GetProperty("LayerId").GetInt32().Should().Be(0);
            var removed = removal.GetProperty("@removed");
            removed.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["reason"]);
            removed.GetProperty("reason").GetString().Should().Be(
                "changed",
                "the server reports that the row no longer belongs to the query; it does not disclose whether the row was deleted or merely stopped matching");
        }

        var update = afterRemovals.Changes.Single(change => ObjectIdOf(change) == 74001);
        update.GetProperty("name").GetString().Should().Be("kept-updated");

        // Replaying the terminal token must not re-emit the removals.
        var replay = await CollectDeltaAsync(afterRemovals.DeltaLink!, tracked: false);
        replay.Changes.Should().BeEmpty("replaying a terminal delta token yields no change at all, not a repeat of the last one");

        // A change set larger than the page size, then a delete committed between the
        // first page and the deltaLink.
        await ExecuteAsync(connection, $$"""
            UPDATE {{schema}}.features SET attributes = '{"name":"paging-updated"}', updated_at = '{{FrozenTimestamp}}' WHERE objectid = 74004;
            UPDATE {{schema}}.features SET attributes = '{"name":"kept-again"}', updated_at = '{{FrozenTimestamp}}' WHERE objectid = 74001;
            """);

        var firstPage = await ReadPageAsync(replay.DeltaLink!, tracked: false);
        firstPage.Changes.Should().ContainSingle("the page size is one");
        firstPage.NextLink.Should().NotBeNull("a change set larger than the page size pages");

        await ExecuteAsync(connection, $$"""
            DELETE FROM {{schema}}.features WHERE objectid = 74005;
            """);

        var remainder = await CollectDeltaAsync(firstPage.NextLink!, tracked: false);
        var pagedChanges = firstPage.Changes.Concat(remainder.Changes).ToArray();
        pagedChanges.Select(ObjectIdOf).Should().BeEquivalentTo(
            new long[] { 74001, 74004 },
            "the remaining pages are served from the frozen change set the first page was computed from");
        pagedChanges.Any(IsRemoval).Should().BeFalse("the mid-paging delete is not retrofitted into a page image already in flight");

        var afterPaging = await CollectDeltaAsync(remainder.DeltaLink!, tracked: false);
        afterPaging.Changes.Should().ContainSingle("the delete committed while paging is delivered by the next poll");
        var midPageRemoval = afterPaging.Changes.Single();
        ObjectIdOf(midPageRemoval).Should().Be(74005);
        IsRemoval(midPageRemoval).Should().BeTrue();

        var settled = await CollectDeltaAsync(afterPaging.DeltaLink!, tracked: false);
        settled.Changes.Should().BeEmpty("the subscriber has caught up and the delete is not delivered a second time");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static long ObjectIdOf(JsonElement change) => change.GetProperty("ObjectId").GetInt64();

    private static bool IsRemoval(JsonElement change) => change.TryGetProperty("@removed", out _);

    private sealed record DeltaPage(JsonElement[] Changes, string? NextLink, string? DeltaLink);

    private async Task<DeltaPage> ReadPageAsync(string link, bool tracked)
    {
        // A root-relative URL is also an absolute file URI on Unix; preserve its query
        // string instead of converting it to a file path.
        using var request = new HttpRequestMessage(HttpMethod.Get, link.StartsWith('/') ? link : new Uri(link).PathAndQuery);
        if (tracked)
        {
            request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");
        }

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} must succeed", request.RequestUri);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var changes = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.Clone()).ToArray();
        var nextLink = document.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        var deltaLink = document.RootElement.TryGetProperty("@odata.deltaLink", out var delta) ? delta.GetString() : null;
        return new DeltaPage(changes, nextLink, deltaLink);
    }

    private async Task<DeltaPage> CollectDeltaAsync(string link, bool tracked)
    {
        var changes = new List<JsonElement>();
        for (var page = 0; page < 20; page++)
        {
            var current = await ReadPageAsync(link, tracked && page == 0);
            changes.AddRange(current.Changes);
            if (current.NextLink is null)
            {
                current.DeltaLink.Should().NotBeNull("the final page carries the delta link");
                return new DeltaPage(changes.ToArray(), null, current.DeltaLink);
            }

            link = current.NextLink;
        }

        throw new InvalidOperationException("Paging must terminate without unbounded duplicates.");
    }
}
