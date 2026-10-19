// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Providers;
using Honua.TestKit;
using Npgsql;
using Xunit;

namespace Honua.Server.Tests.Features.Geocoding;

/// <summary>
/// Integration coverage for the self-hosted, PostGIS-backed local geocoder (#2151). Loads a small
/// reference fixture into an isolated PostGIS database and geocodes/reverse-geocodes known addresses,
/// fully offline (no external provider calls).
/// </summary>
[Trait("Tier", "Fast")]
public sealed class LocalPostgisGeocodeProviderIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private NpgsqlDataSource _dataSource = null!;
    private LocalPostgisGeocodeProvider _provider = null!;
    private string? _databaseName;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var connectionString = await _fixture.CreateIsolatedDatabaseAsync(nameof(LocalPostgisGeocodeProviderIntegrationTests));
        _databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;
        _dataSource = NpgsqlDataSource.Create(connectionString);

        await using (var command = _dataSource.CreateCommand("""
            CREATE TABLE honua_geocode_reference (
                id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                display_name  text NOT NULL,
                search_text   text NOT NULL,
                address_number text,
                street_name   text,
                city          text,
                region        text,
                postal_code   text,
                country       text,
                neighborhood  text,
                address_type  text,
                geom          geometry(Point, 4326) NOT NULL
            );
            CREATE INDEX ix_local_geocode_geom ON honua_geocode_reference USING gist (geom);
            CREATE INDEX ix_local_geocode_search ON honua_geocode_reference (search_text text_pattern_ops);
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await InsertReferenceAsync(
            "380 New York St, Redlands, CA 92373",
            "380 new york st redlands ca 92373",
            "380", "New York St", "Redlands", "CA", "92373", "US", "PointAddress",
            -117.1956, 34.0566);

        await InsertReferenceAsync(
            "1 Microsoft Way, Redmond, WA 98052",
            "1 microsoft way redmond wa 98052",
            "1", "Microsoft Way", "Redmond", "WA", "98052", "US", "PointAddress",
            -122.1298, 47.6396);

        _provider = new LocalPostgisGeocodeProvider(
            _dataSource,
            new LocalGeocoderProviderConfiguration { Schema = "public", Table = "honua_geocode_reference" });
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();

        if (_databaseName is not null)
        {
            await _fixture.DropDatabaseAsync(_databaseName);
        }

        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task ForwardGeocodeAsync_KnownAddress_ReturnsCandidate()
    {
        var results = await _provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("380 New York St Redlands", 5, 4326),
            CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal("380 New York St, Redlands, CA 92373", candidate.Address);
        Assert.Equal(-117.1956, candidate.X, 4);
        Assert.Equal(34.0566, candidate.Y, 4);
        Assert.True(candidate.Score > 0);
        Assert.Equal("Redlands", candidate.StructuredAddress?.City);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_CountryCodes_RestrictsStoredCountryBeforeRankingAndLimit()
    {
        // The Canadian record matches the query more tightly and would rank
        // first without a SQL-level country predicate before LIMIT 1.
        await InsertReferenceAsync(
            "380 New York St, Toronto, ON",
            "380 new york st redlands",
            "380", "New York St", "Toronto", "ON", "M5H 2N2", "CA", "PointAddress",
            -79.3832, 43.6532);

        var unrestricted = await _provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("380 New York St Redlands", 1, 4326));
        Assert.Equal("380 New York St, Toronto, ON", Assert.Single(unrestricted).Address);

        var us = await _provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("380 New York St Redlands", 1, 4326, CountryCodes: " us "));
        Assert.Equal("380 New York St, Redlands, CA 92373", Assert.Single(us).Address);

        var ca = await _provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("380 New York St Redlands", 1, 4326, CountryCodes: "CA"));
        Assert.Equal("380 New York St, Toronto, ON", Assert.Single(ca).Address);

        var multiple = await _provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("380 New York St Redlands", 1, 4326, CountryCodes: "ca, US"));
        Assert.Equal("380 New York St, Toronto, ON", Assert.Single(multiple).Address);

        var excluded = await _provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Microsoft Way Redmond", 1, 4326, CountryCodes: "ca"));
        Assert.Empty(excluded);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_MalformedCountryCodes_RefusesBeforeSearching()
    {
        foreach (var filter in new[] { "USA", "US,", ",US", "U1", "üS", new string('U', 1025) })
        {
            var error = await Assert.ThrowsAsync<GeocodeRequestException>(() =>
                _provider.ForwardGeocodeAsync(
                    new ForwardGeocodeRequest("380 New York St Redlands", 1, 4326, CountryCodes: filter)));
            Assert.Equal("countryCodes", error.ParameterName);
        }
    }

    [Fact]
    public async Task BatchGeocodeAsync_CountryCodes_RestrictsEachInputAndPreservesResultIds()
    {
        await InsertReferenceAsync(
            "380 New York St, Toronto, ON",
            "380 new york st redlands",
            "380", "New York St", "Toronto", "ON", "M5H 2N2", "CA", "PointAddress",
            -79.3832, 43.6532);

        var results = await _provider.BatchGeocodeAsync(
            new BatchGeocodeRequest(
                ["380 New York St Redlands", "1 Microsoft Way Redmond"],
                CountryCodes: " ca "));

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsMatch);
        Assert.Equal("380 New York St, Toronto, ON", results[0].Address);
        Assert.Equal("0", results[0].Attributes[GeocodeCandidate.ResultIdAttribute]);
        Assert.False(results[1].IsMatch);
        Assert.Equal("1", results[1].Attributes[GeocodeCandidate.ResultIdAttribute]);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_SeventyThousandTokens_ThrowsGeocodeRequestException()
    {
        var query = string.Join(' ', Enumerable.Repeat("e", 70_000));

        var exception = await Assert.ThrowsAsync<GeocodeRequestException>(() =>
            _provider.ForwardGeocodeAsync(new ForwardGeocodeRequest(query, 5, 4326), CancellationToken.None));

        Assert.Contains("Invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_StructuredInput_ReturnsCandidate()
    {
        var request = new ForwardGeocodeRequest("ignored", 5, 4326, InputType: GeocodeInputType.Structured)
        {
            StructuredAddress = new StructuredAddress
            {
                StreetName = "Microsoft Way",
                City = "Redmond",
                Region = "WA",
                PostalCode = "98052"
            }
        };

        var results = await _provider.ForwardGeocodeAsync(request, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal("1 Microsoft Way, Redmond, WA 98052", candidate.Address);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_KnownPoint_ReturnsNearestAddress()
    {
        var match = await _provider.ReverseGeocodeAsync(
            new ReverseGeocodeRequest(-117.1957, 34.0567, 4326, DistanceMeters: 500),
            CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("380 New York St, Redlands, CA 92373", match.Address);
        Assert.NotNull(match.DistanceMeters);
        Assert.True(match.DistanceMeters < 500);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_NoNearbyRecord_ReturnsNull()
    {
        var match = await _provider.ReverseGeocodeAsync(
            new ReverseGeocodeRequest(0.0, 0.0, 4326, DistanceMeters: 100),
            CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task SuggestAsync_Prefix_ReturnsSuggestion()
    {
        var suggestions = await _provider.SuggestAsync(
            new SuggestGeocodeRequest("1 microsoft", 5),
            CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("1 Microsoft Way, Redmond, WA 98052", suggestion.Text);
    }

    private async Task InsertReferenceAsync(
        string displayName,
        string searchText,
        string addressNumber,
        string streetName,
        string city,
        string region,
        string postalCode,
        string country,
        string addressType,
        double longitude,
        double latitude)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO honua_geocode_reference
                (display_name, search_text, address_number, street_name, city, region, postal_code, country, address_type, geom)
            VALUES
                (@display, @search, @num, @street, @city, @region, @postal, @country, @type,
                 ST_SetSRID(ST_MakePoint(@lon, @lat), 4326))
            """);

        command.Parameters.AddWithValue("display", displayName);
        command.Parameters.AddWithValue("search", searchText);
        command.Parameters.AddWithValue("num", addressNumber);
        command.Parameters.AddWithValue("street", streetName);
        command.Parameters.AddWithValue("city", city);
        command.Parameters.AddWithValue("region", region);
        command.Parameters.AddWithValue("postal", postalCode);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("type", addressType);
        command.Parameters.AddWithValue("lon", longitude);
        command.Parameters.AddWithValue("lat", latitude);

        await command.ExecuteNonQueryAsync();
    }
}
