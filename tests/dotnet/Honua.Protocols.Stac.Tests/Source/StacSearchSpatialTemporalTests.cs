// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Discriminating coverage for the three <c>/stac/search</c> parameters that honua-server#4425
/// found unproven on the GA catalog surface: <c>bbox</c> (asserted only against a world bbox or a
/// status code), <c>datetime</c> (exclusion only — no test ever asserted a matching item is
/// present) and <c>intersects</c> (no .NET test existed at all).
/// </summary>
/// <remarks>
/// <para>
/// Each test seeds two items whose only relevant difference is the property under test and then
/// asserts an <b>exact id set</b>: the matching item present and the non-matching item absent. A
/// parameter that was parsed and never applied — which is the silent-wrong-data failure mode for a
/// catalog — fails the absence half, and a parameter that over-filters fails the presence half.
/// Per-row re-checks of the same predicate, or a whole-world bbox, cannot distinguish either.
/// </para>
/// <para>
/// The strong equivalent already exists in the Python certification lane
/// (<c>tests/python/stac_client/test_cert_common_core.py</c>); this brings the same discipline into
/// the .NET suite, which runs on the required gate.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Stac)]
[Operation(Operations.StacSearch)]
public sealed class StacSearchSpatialTemporalTests : IAsyncLifetime
{
    private const double InsideLon = -122.4194;
    private const double InsideLat = 37.7749;
    private const double OutsideLon = 2.3522;
    private const double OutsideLat = 48.8566;

    private readonly WebAppFixture _fixture = new();
    private string _runId = null!;
    private string _insideId = null!;
    private string _outsideId = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _runId = Guid.NewGuid().ToString("N")[..8];
        _insideId = $"inside-{_runId}";
        _outsideId = $"outside-{_runId}";
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string CollectionId => WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_Bbox_ReturnsTheItemInsideAndExcludesTheOneOutside()
    {
        await SeedPointsAsync();

        // A bbox tight around San Francisco; Paris is 9,000 km away and must not appear.
        var ids = await SearchIdsAsync(
            $"/stac/search?collections={CollectionId}&bbox=-123,37,-122,38&limit=100");

        ids.Should().Contain(_insideId, "the item inside the bbox must be returned");
        ids.Should().NotContain(
            _outsideId,
            "a bbox that was parsed and never applied would return the far-away item too — the " +
            "exclusion half is the assertion that can actually fail");
    }

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_BboxOnTheOtherSideOfTheWorld_ReturnsNeitherItem()
    {
        await SeedPointsAsync();

        var ids = await SearchIdsAsync(
            $"/stac/search?collections={CollectionId}&bbox=140,-40,150,-30&limit=100");

        ids.Should().NotContain(_insideId);
        ids.Should().NotContain(_outsideId);
    }

    [IntegrationTest]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_IntersectsPolygon_ReturnsTheItemInsideAndExcludesTheOneOutside()
    {
        // honua-server#4425: `git grep 'intersects=' -- tests/` returned no /stac/ hit at all, so
        // this parameter of the GA catalog surface had zero .NET coverage.
        await SeedPointsAsync();

        var ids = await SearchPostIdsAsync(new
        {
            collections = new[] { CollectionId },
            limit = 100,
            intersects = new
            {
                type = "Polygon",
                coordinates = new[]
                {
                    new[]
                    {
                        new[] { -123.0, 37.0 },
                        new[] { -122.0, 37.0 },
                        new[] { -122.0, 38.0 },
                        new[] { -123.0, 38.0 },
                        new[] { -123.0, 37.0 }
                    }
                }
            }
        });

        ids.Should().Contain(_insideId);
        ids.Should().NotContain(_outsideId, "the intersects geometry must actually filter");
    }

    [IntegrationTest]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_IntersectsPolygonElsewhere_ReturnsNeitherItem()
    {
        await SeedPointsAsync();

        var ids = await SearchPostIdsAsync(new
        {
            collections = new[] { CollectionId },
            limit = 100,
            intersects = new
            {
                type = "Polygon",
                coordinates = new[]
                {
                    new[]
                    {
                        new[] { 140.0, -40.0 },
                        new[] { 150.0, -40.0 },
                        new[] { 150.0, -30.0 },
                        new[] { 140.0, -30.0 },
                        new[] { 140.0, -40.0 }
                    }
                }
            }
        });

        ids.Should().NotContain(_insideId);
        ids.Should().NotContain(_outsideId);
    }

    [IntegrationTest]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_IntersectsPoint_MatchesOnlyTheItemAtThatPoint()
    {
        await SeedPointsAsync();

        var ids = await SearchPostIdsAsync(new
        {
            collections = new[] { CollectionId },
            limit = 100,
            intersects = new
            {
                type = "Point",
                coordinates = new[] { InsideLon, InsideLat }
            }
        });

        ids.Should().Contain(_insideId, "an exact-coordinate point must intersect the item stored there");
        ids.Should().NotContain(_outsideId);
    }

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_Datetime_ReturnsTheItemInsideTheIntervalAndExcludesTheOneOutside()
    {
        // The only datetime coverage asserted that an interval matched NOTHING, against a fixture
        // where no row had a temporal value — so a datetime filter that was inert passed. Seed two
        // dated items and require the interval to separate them.
        await SeedDatedPointsAsync();

        var ids = await SearchIdsAsync(
            $"/stac/search?collections={CollectionId}" +
            "&datetime=2026-03-01T00:00:00Z/2026-03-31T23:59:59Z&limit=100");

        ids.Should().Contain(_insideId, "an item dated 2026-03-15 falls inside the March interval");
        ids.Should().NotContain(_outsideId, "an item dated 2025-06-01 falls outside it");
    }

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_DatetimeInstant_MatchesOnlyTheItemAtThatInstant()
    {
        await SeedDatedPointsAsync();

        var ids = await SearchIdsAsync(
            $"/stac/search?collections={CollectionId}&datetime=2026-03-15T12:00:00Z&limit=100");

        ids.Should().Contain(_insideId);
        ids.Should().NotContain(_outsideId);
    }

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_OpenEndedDatetime_ExcludesEverythingBeforeTheLowerBound()
    {
        await SeedDatedPointsAsync();

        var ids = await SearchIdsAsync(
            $"/stac/search?collections={CollectionId}&datetime=2026-01-01T00:00:00Z/..&limit=100");

        ids.Should().Contain(_insideId);
        ids.Should().NotContain(_outsideId, "the 2025 item is before the open-ended lower bound");
    }

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_Collections_ExcludesItemsFromOtherCollections()
    {
        // The existing collections coverage re-checks the same predicate per row, which cannot
        // fail; this asserts an item from a different collection is absent.
        await SeedPointsAsync();
        var otherCollectionItem = $"other-collection-{_runId}";
        await SeedAsync(
            1,
            $"POINT({InsideLon.ToString(CultureInfo.InvariantCulture)} {InsideLat.ToString(CultureInfo.InvariantCulture)})",
            new Dictionary<string, string?> { ["name"] = otherCollectionItem, ["id"] = otherCollectionItem });

        var ids = await SearchIdsAsync($"/stac/search?collections={CollectionId}&limit=100");

        ids.Should().Contain(_insideId);
        ids.Should().NotContain(
            otherCollectionItem,
            "restricting to one collection must exclude another collection's items");
    }

    private async Task SeedPointsAsync()
    {
        await SeedAsync(
            WebAppFixture.TestLayerId,
            $"POINT({InsideLon.ToString(CultureInfo.InvariantCulture)} {InsideLat.ToString(CultureInfo.InvariantCulture)})",
            new Dictionary<string, string?> { ["name"] = _insideId, ["id"] = _insideId });
        await SeedAsync(
            WebAppFixture.TestLayerId,
            $"POINT({OutsideLon.ToString(CultureInfo.InvariantCulture)} {OutsideLat.ToString(CultureInfo.InvariantCulture)})",
            new Dictionary<string, string?> { ["name"] = _outsideId, ["id"] = _outsideId });
    }

    private async Task SeedDatedPointsAsync()
    {
        // The seeded layer declares `timestamp` as its start-time field, which is what the STAC
        // adapter maps onto an item's datetime.
        await SeedAsync(
            WebAppFixture.TestLayerId,
            $"POINT({InsideLon.ToString(CultureInfo.InvariantCulture)} {InsideLat.ToString(CultureInfo.InvariantCulture)})",
            new Dictionary<string, string?>
            {
                ["name"] = _insideId,
                ["id"] = _insideId,
                ["timestamp"] = "2026-03-15T12:00:00Z"
            });
        await SeedAsync(
            WebAppFixture.TestLayerId,
            $"POINT({OutsideLon.ToString(CultureInfo.InvariantCulture)} {OutsideLat.ToString(CultureInfo.InvariantCulture)})",
            new Dictionary<string, string?>
            {
                ["name"] = _outsideId,
                ["id"] = _outsideId,
                ["timestamp"] = "2025-06-01T12:00:00Z"
            });
    }

    /// <summary>
    /// Seeds a row with real geometry. The shared <c>InsertFeatureAsync</c> helper stores a NULL
    /// geometry, which silently makes every spatial assertion in this class vacuous.
    /// </summary>
    private async Task SeedAsync(int layerId, string wkt, IReadOnlyDictionary<string, string?> attributes)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, ST_SetSRID(ST_GeomFromText(@wkt), 4326), @attributes::jsonb);
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("wkt", wkt);
        command.Parameters.AddWithValue("attributes", JsonSerializer.Serialize(attributes));
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task<string[]> SearchIdsAsync(string url)
    {
        using var response = await _fixture.Client.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        return ReadIds(content);
    }

    private async Task<string[]> SearchPostIdsAsync(object body)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/stac/search", body);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        return ReadIds(content);
    }

    private static string[] ReadIds(string content)
    {
        using var json = JsonDocument.Parse(content);
        return [.. json.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()!)];
    }
}
