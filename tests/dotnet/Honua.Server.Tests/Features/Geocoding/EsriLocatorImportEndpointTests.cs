// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Server.Tests.Features.Geocoding;

/// <summary>
/// Integration coverage for the Esri <c>.loc</c>/<c>.lox</c> locator import (#2152): a classic
/// text locator plus CSV reference data is imported into the local PostGIS geocoder and then
/// served through GeocodeServer (forward, reverse, suggest) fully offline; unsupported locator
/// constructs surface in an explicit translation report. Fixtures are deterministic in-test
/// payloads — no licensed Esri software is involved.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Geocoding)]
public sealed class EsriLocatorImportEndpointTests : IAsyncLifetime
{
    private const string LocatorName = "RedlandsStreets";

    private const string ClassicLoc = """
        ; US Streets style address locator (deterministic test fixture)
        Version = 8.1
        CLSID = {AE5A3A0E-F756-11D2-9F4F-00C04F8ED1C4}
        Category = Address
        Fields = SingleLine
        MinimumMatchScore = 60
        MinimumCandidateScore = 10
        SpellingSensitivity = 80
        SideOffset = 20
        SideOffsetUnits = Feet
        EndOffset = 3
        MatchIfScoresTie = TRUE
        Interpolate = TRUE
        BatchPresenceThreshold = 0.8
        """;

    private const string ReferenceCsv =
        "HOUSE_NUM,STREET_NAME,CITY,STATE,ZIP,COUNTRY,POINT_X,POINT_Y,NOTES\n" +
        "380,New York St,Redlands,CA,92373,US,-117.1956,34.0566,esri hq\n" +
        "1,Microsoft Way,Redmond,WA,98052,US,-122.1298,47.6396,msft hq\n" +
        "bad,Row,No,Coords,00000,US,not-a-number,34.0,broken\n" +
        "7,NaN St,Nowhere,ZZ,11111,US,NaN,NaN,non-finite coords\n";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _adminClient = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _fixture.ConfigureWebHost(builder =>
        {
            // Providers:Local:Enabled is read while services are being registered (AddGeocoding),
            // before a ConfigureAppConfiguration callback added here would apply — UseSetting
            // (mirrors the licensing pattern in GeocodingEndpointTests) pushes the values in early
            // enough to be honored. Dev-auth bypass is disabled so the admin-auth test is real;
            // the admin client authenticates via the fixture's shared admin password.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Geocoding:Enabled", "true");
            builder.UseSetting("Geocoding:DefaultProvider", "local");
            builder.UseSetting("Geocoding:LocatorName", LocatorName);
            builder.UseSetting("Geocoding:DefaultSpatialReferenceWkid", "4326");
            builder.UseSetting("Geocoding:Providers:Local:Enabled", "true");
        });

        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateAdminClient();
        _client = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_ClassicLocatorWithReferenceData_ServesGeocodeServerRoundTrip()
    {
        using var content = BuildImportForm(includeReference: true, includeIndex: true);
        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Import failed: {response.StatusCode}: {body}");

        using var payload = JsonDocument.Parse(body);
        var data = payload.RootElement.GetProperty("data");

        Assert.Equal(LocatorName, data.GetProperty("locatorName").GetString());
        Assert.Equal("local", data.GetProperty("provider").GetString());
        Assert.True(data.GetProperty("referenceDataImported").GetBoolean());
        Assert.Equal(2, data.GetProperty("recordsImported").GetInt32());
        Assert.Equal(2, data.GetProperty("recordsSkipped").GetInt32());

        // Match settings are recorded from the source locator.
        var matchSettings = data.GetProperty("matchSettings");
        Assert.Equal(60, matchSettings.GetProperty("minimumMatchScore").GetDouble());
        Assert.Equal(80, matchSettings.GetProperty("spellingSensitivity").GetDouble());

        // The unsupported construct and the regenerated .lox index are reported explicitly.
        var report = data.GetProperty("report").EnumerateArray().ToArray();
        Assert.Contains(report, e =>
            e.GetProperty("item").GetString() == "BatchPresenceThreshold" &&
            e.GetProperty("status").GetString() == "unsupported");
        Assert.Contains(report, e =>
            e.GetProperty("item").GetString() == "redlands.lox" &&
            e.GetProperty("status").GetString() == "regenerated");

        // The unmapped CSV column is reported, not silently dropped.
        Assert.Contains(report, e =>
            e.GetProperty("item").GetString() == "NOTES" &&
            e.GetProperty("status").GetString() == "ignored");

        // Round-trip: the imported locator serves forward geocode via GeocodeServer...
        using var forward = await _client.GetAsync(
            $"/rest/services/{LocatorName}/GeocodeServer/findAddressCandidates?singleLine=380+New+York+St+Redlands&f=json");
        Assert.Equal(HttpStatusCode.OK, forward.StatusCode);
        using var forwardPayload = JsonDocument.Parse(await forward.Content.ReadAsStringAsync());
        var candidates = forwardPayload.RootElement.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0);
        var candidate = candidates[0];
        Assert.Equal("380 New York St, Redlands, CA 92373", candidate.GetProperty("address").GetString());
        Assert.Equal(-117.1956, candidate.GetProperty("location").GetProperty("x").GetDouble(), 4);
        Assert.Equal(34.0566, candidate.GetProperty("location").GetProperty("y").GetDouble(), 4);

        // ...a structured request whose country token must be present in search_text...
        using var structured = await _client.GetAsync(
            $"/rest/services/{LocatorName}/GeocodeServer/findAddressCandidates?Address=380+New+York+St&City=Redlands&CountryCode=US&f=json");
        Assert.Equal(HttpStatusCode.OK, structured.StatusCode);
        using var structuredPayload = JsonDocument.Parse(await structured.Content.ReadAsStringAsync());
        Assert.True(structuredPayload.RootElement.GetProperty("candidates").GetArrayLength() > 0);

        // ...reverse geocode...
        using var reverse = await _client.GetAsync(
            $"/rest/services/{LocatorName}/GeocodeServer/reverseGeocode?location=-117.1957,34.0567&f=json");
        Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        using var reversePayload = JsonDocument.Parse(await reverse.Content.ReadAsStringAsync());
        Assert.Equal(
            "380 New York St, Redlands, CA 92373",
            reversePayload.RootElement.GetProperty("address").GetProperty("Match_addr").GetString());

        // ...and suggest.
        using var suggest = await _client.GetAsync(
            $"/rest/services/{LocatorName}/GeocodeServer/suggest?text=1+microsoft&f=json");
        Assert.Equal(HttpStatusCode.OK, suggest.StatusCode);
        using var suggestPayload = JsonDocument.Parse(await suggest.Content.ReadAsStringAsync());
        var suggestions = suggestPayload.RootElement.GetProperty("suggestions");
        Assert.True(suggestions.GetArrayLength() > 0);
        Assert.Equal("1 Microsoft Way, Redmond, WA 98052", suggestions[0].GetProperty("text").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_WithoutReferenceData_ParsesAndClassifiesOnly()
    {
        using var content = BuildImportForm(includeReference: false, includeIndex: false);
        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = payload.RootElement.GetProperty("data");

        Assert.False(data.GetProperty("referenceDataImported").GetBoolean());
        Assert.Equal(0, data.GetProperty("recordsImported").GetInt32());
        Assert.True(data.GetProperty("report").GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_BinaryProLocator_Returns400WithExplicitError()
    {
        using var content = new MultipartFormDataContent();
        var binary = new ByteArrayContent([0x50, 0x4B, 0x00, 0x01, 0x02, 0x03]);
        binary.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(binary, "locator", "pro-locator.loc");

        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("binary", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_MissingLocatorFile_Returns400()
    {
        using var content = new MultipartFormDataContent();
        var csv = new ByteArrayContent(Encoding.UTF8.GetBytes(ReferenceCsv));
        csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(csv, "referenceData", "redlands.csv");

        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_ReferenceDataWithoutCoordinateColumns_Returns400()
    {
        using var content = new MultipartFormDataContent();
        AddLocPart(content);
        var csv = new ByteArrayContent(Encoding.UTF8.GetBytes("ADDRESS,CITY\n380 New York St,Redlands\n"));
        csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(csv, "referenceData", "no-coords.csv");

        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("longitude/latitude", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_WithoutAdminCredentials_IsRejected()
    {
        using var content = BuildImportForm(includeReference: false, includeIndex: false);
        using var response = await _client.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403 for anonymous import, got {(int)response.StatusCode}");
    }

    private static MultipartFormDataContent BuildImportForm(bool includeReference, bool includeIndex)
    {
        var content = new MultipartFormDataContent();
        AddLocPart(content);

        if (includeIndex)
        {
            // Deterministic stand-in for the opaque binary index sidecar.
            var lox = new ByteArrayContent([0x00, 0x01, 0x02, 0x03]);
            lox.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(lox, "index", "redlands.lox");
        }

        if (includeReference)
        {
            var csv = new ByteArrayContent(Encoding.UTF8.GetBytes(ReferenceCsv));
            csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            content.Add(csv, "referenceData", "redlands.csv");
        }

        content.Add(new StringContent(LocatorName), "locatorName");
        return content;
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_FieldMapColumnServingMultipleRoles_Succeeds()
    {
        using var content = BuildImportForm(includeReference: true, includeIndex: false);
        content.Add(new StringContent("{\"displayName\":\"STREET_NAME\",\"streetName\":\"STREET_NAME\"}"), "fieldMap");

        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Import failed: {response.StatusCode}: {body}");
        using var payload = JsonDocument.Parse(body);
        var report = payload.RootElement.GetProperty("data").GetProperty("report").EnumerateArray().ToArray();
        Assert.Contains(report, static e =>
            e.GetProperty("item").GetString() == "STREET_NAME" &&
            e.GetProperty("status").GetString() == "supported" &&
            e.GetProperty("detail").GetString()!.Contains("displayName", StringComparison.Ordinal) &&
            e.GetProperty("detail").GetString()!.Contains("streetName", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_NullFieldMapValue_Returns400()
    {
        using var content = BuildImportForm(includeReference: true, includeIndex: false);
        content.Add(new StringContent("{\"x\":null}"), "fieldMap");

        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("must be a non-empty CSV column name", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/locators/import")]
    public async Task Import_MismatchedLocatorName_Returns400()
    {
        using var content = new MultipartFormDataContent();
        AddLocPart(content);
        content.Add(new StringContent("SomeOtherLocator"), "locatorName");

        using var response = await _adminClient.PostAsync("/api/v1/admin/geocoding/locators/import", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not match the geocode service name", body, StringComparison.Ordinal);
    }

    private static void AddLocPart(MultipartFormDataContent content)
    {
        var loc = new ByteArrayContent(Encoding.UTF8.GetBytes(ClassicLoc));
        loc.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(loc, "locator", "redlands.loc");
    }
}
