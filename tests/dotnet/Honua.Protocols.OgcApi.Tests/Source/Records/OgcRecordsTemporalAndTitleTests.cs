// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

/// <summary>
/// Gives the OGC API Records surface a fixture with discriminating power over time and title
/// (honua-server#4425).
/// </summary>
/// <remarks>
/// <para>
/// The shared Records fixture seeds four records, none with a temporal value and none with a title
/// distinct from its name. Consequently the only <c>datetime</c> test asserted that the filter
/// <em>did nothing</em> — and it could not have done otherwise, because both record factories
/// hard-coded <c>Modified: null</c> and the filter short-circuited before its comparison ran. No
/// query could exclude a record by time, and the mismatch between the shipped coverage document
/// (which says <c>q</c> searches title) and the implementation (which searched name) was invisible
/// because <c>Title ?? Name</c> collapsed.
/// </para>
/// <para>
/// These tests set a real <c>updatedAt</c> and a title that shares no token with the record's name,
/// then assert both halves: the record is returned when the interval covers its timestamp and
/// absent when it does not, and <c>q</c> matches on the title. The #1988 guarantee — a record with
/// no temporal value is never excluded by <c>datetime</c> — is asserted alongside, against a record
/// that genuinely has no timestamp.
/// </para>
/// </remarks>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsTemporalAndTitleTests : IAsyncLifetime
{
    private const string CatalogId = "honua-catalog";
    private const string DistinctTitle = "Bathymetric Soundings Archive";
    private static readonly DateTimeOffset RecordUpdatedAt = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.MutateV2ResourceObjectMetadata(
            WebAppFixture.TestLayerId,
            metadata => metadata with { Title = DistinctTitle, UpdatedAt = RecordUpdatedAt });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string TimedRecordId => $"layer:{WebAppFixture.TestLayerId}";

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_DatetimeIntervalCoveringTheRecord_ReturnsIt()
    {
        var ids = await GetRecordIdsAsync(
            "datetime=" + Uri.EscapeDataString("2026-03-01T00:00:00Z/2026-03-31T23:59:59Z") + "&limit=100");

        ids.Should().Contain(
            TimedRecordId,
            "a record whose timestamp falls inside the requested interval must be returned — no " +
            "test previously asserted the inclusion half at all");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_DatetimeIntervalBeforeTheRecord_ExcludesIt()
    {
        var ids = await GetRecordIdsAsync(
            "datetime=" + Uri.EscapeDataString("2020-01-01T00:00:00Z/2020-12-31T23:59:59Z") + "&limit=100");

        ids.Should().NotContain(
            TimedRecordId,
            "a record whose timestamp falls outside the interval must be excluded — this is the " +
            "assertion that a provably inert datetime filter cannot pass");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_DatetimeIntervalAfterTheRecord_ExcludesIt()
    {
        var ids = await GetRecordIdsAsync(
            "datetime=" + Uri.EscapeDataString("2030-01-01T00:00:00Z/2030-12-31T23:59:59Z") + "&limit=100");

        ids.Should().NotContain(TimedRecordId);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_OpenEndedDatetime_BoundsTheRecordOnOneSideOnly()
    {
        (await GetRecordIdsAsync("datetime=" + Uri.EscapeDataString("2026-01-01T00:00:00Z/..") + "&limit=100"))
            .Should().Contain(TimedRecordId, "the record is after the lower bound");
        (await GetRecordIdsAsync("datetime=" + Uri.EscapeDataString("2027-01-01T00:00:00Z/..") + "&limit=100"))
            .Should().NotContain(TimedRecordId, "the record is before that lower bound");
        (await GetRecordIdsAsync("datetime=" + Uri.EscapeDataString("../2025-01-01T00:00:00Z") + "&limit=100"))
            .Should().NotContain(TimedRecordId, "the record is after that upper bound");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_Datetime_StillKeepsRecordsThatHaveNoTemporalValue()
    {
        // The #1988 guarantee, now asserted against a fixture where OTHER records do have a
        // timestamp — so "kept" is a real decision rather than the only possible outcome.
        var ids = await GetRecordIdsAsync(
            "datetime=" + Uri.EscapeDataString("2020-01-01T00:00:00Z/2020-12-31T23:59:59Z") + "&limit=100");

        ids.Should().Contain(
            "service:test",
            "a record with no temporal value carries no instant to test, so datetime must not drop it");
        ids.Should().NotContain(TimedRecordId, "while the timed record in the same response is excluded");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetItems_QMatchesTheRecordTitle()
    {
        // The shipped coverage document states q searches over id, title and description. The
        // haystack omitted the title, and the fixture could not detect it because every seeded
        // title was null.
        var ids = await GetRecordIdsAsync("q=Bathymetric&limit=100");

        ids.Should().Contain(TimedRecordId, "q must match a term that appears only in the title");
        ids.Should().NotContain("service:test", "and must not match records whose text lacks the term");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetItem_ReturnsTheDistinctTitleRatherThanTheName()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items/{Uri.EscapeDataString(TimedRecordId)}");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("properties").GetProperty("title").GetString().Should().Be(
            DistinctTitle,
            "the record title must come from the catalog title, not collapse to the machine name");
    }

    private async Task<string[]> GetRecordIdsAsync(string query)
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/{CatalogId}/items?{query}");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var json = JsonDocument.Parse(content);
        return [.. json.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()!)];
    }
}
