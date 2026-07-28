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
/// Integration coverage for the geocoder reference data import: CSV reference data is loaded into
/// the local PostGIS geocoder and then served through GeocodeServer (forward, structured, reverse,
/// suggest) fully offline; every CSV column lands in an explicit report and invalid rows are
/// skipped with reasons. Fixtures are deterministic in-test payloads.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Geocoding)]
public sealed class GeocoderReferenceDataImportEndpointTests : IAsyncLifetime
{
    private const string ImportRoute = "/api/v1/admin/geocoding/reference-data/import";
    private const string LocatorName = "RedlandsStreets";

    private const string ReferenceCsv =
        "HOUSE_NUM,STREET_NAME,CITY,STATE,ZIP,COUNTRY,POINT_X,POINT_Y,NOTES\n" +
        "380,New York St,Redlands,CA,92373,US,-117.1956,34.0566,sample hq\n" +
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
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_ReferenceCsv_ServesGeocodeServerRoundTrip()
    {
        using var content = BuildImportForm();
        using var response = await _adminClient.PostAsync(ImportRoute, content);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Import failed: {response.StatusCode}: {body}");

        using var payload = JsonDocument.Parse(body);
        var data = payload.RootElement.GetProperty("data");

        Assert.Equal(LocatorName, data.GetProperty("locatorName").GetString());
        Assert.Equal("local", data.GetProperty("provider").GetString());
        Assert.Equal(2, data.GetProperty("recordsImported").GetInt32());
        Assert.Equal(2, data.GetProperty("recordsSkipped").GetInt32());

        // Skipped rows carry reasons (invalid number + non-finite coordinates).
        var skipped = data.GetProperty("skippedRows").EnumerateArray().ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.All(skipped, static r => Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("reason").GetString())));

        // Every CSV column is reported: mapped columns as supported, the rest explicitly ignored.
        var report = data.GetProperty("report").EnumerateArray().ToArray();
        Assert.Contains(report, static e =>
            e.GetProperty("column").GetString() == "STREET_NAME" &&
            e.GetProperty("status").GetString() == "supported");
        Assert.Contains(report, static e =>
            e.GetProperty("column").GetString() == "NOTES" &&
            e.GetProperty("status").GetString() == "ignored");

        // Round-trip: the imported reference data serves forward geocode via GeocodeServer...
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
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_MissingReferenceDataFile_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(LocatorName), "locatorName");

        using var response = await _adminClient.PostAsync(ImportRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("referenceData", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_ReferenceDataWithoutCoordinateColumns_Returns400()
    {
        using var content = new MultipartFormDataContent();
        AddCsvPart(content, "ADDRESS,CITY\n380 New York St,Redlands\n", "no-coords.csv");
        content.Add(new StringContent(LocatorName), "locatorName");

        using var response = await _adminClient.PostAsync(ImportRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("longitude/latitude", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_WithoutAdminCredentials_IsRejected()
    {
        using var content = BuildImportForm();
        using var response = await _client.PostAsync(ImportRoute, content);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403 for anonymous import, got {(int)response.StatusCode}");
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_MalformedMultipartBody_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ImportRoute);
        request.Content = new StringContent("not multipart at all", Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data")
        {
            Parameters = { new NameValueHeaderValue("boundary", "\"missing\"") },
        };

        using var response = await _adminClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be parsed", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_ReplaceWithNoImportableRows_Returns400AndPreservesData()
    {
        using (var seed = BuildImportForm())
        {
            using var seedResponse = await _adminClient.PostAsync(ImportRoute, seed);
            Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);
        }

        using var content = new MultipartFormDataContent();
        AddCsvPart(content,
            "HOUSE_NUM,STREET_NAME,CITY,STATE,ZIP,COUNTRY,POINT_X,POINT_Y\n" +
            "bad,Row,No,Coords,00000,US,not-a-number,also-bad\n",
            "allbad.csv");
        content.Add(new StringContent(LocatorName), "locatorName");

        using var response = await _adminClient.PostAsync(ImportRoute, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("left unchanged", body, StringComparison.Ordinal);

        using var forward = await _client.GetAsync(
            $"/rest/services/{LocatorName}/GeocodeServer/findAddressCandidates?singleLine=380+New+York+St+Redlands&f=json");
        Assert.Equal(HttpStatusCode.OK, forward.StatusCode);
        using var payload = JsonDocument.Parse(await forward.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.GetProperty("candidates").GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_FieldMapColumnServingMultipleRoles_Succeeds()
    {
        using var content = BuildImportForm();
        content.Add(new StringContent("{\"displayName\":\"STREET_NAME\",\"streetName\":\"STREET_NAME\"}"), "fieldMap");

        using var response = await _adminClient.PostAsync(ImportRoute, content);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Import failed: {response.StatusCode}: {body}");
        using var payload = JsonDocument.Parse(body);
        var report = payload.RootElement.GetProperty("data").GetProperty("report").EnumerateArray().ToArray();
        Assert.Contains(report, static e =>
            e.GetProperty("column").GetString() == "STREET_NAME" &&
            e.GetProperty("status").GetString() == "supported" &&
            e.GetProperty("detail").GetString()!.Contains("displayName", StringComparison.Ordinal) &&
            e.GetProperty("detail").GetString()!.Contains("streetName", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_NullFieldMapValue_Returns400()
    {
        using var content = BuildImportForm();
        content.Add(new StringContent("{\"x\":null}"), "fieldMap");

        using var response = await _adminClient.PostAsync(ImportRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("must be a non-empty CSV column name", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("POST /api/v1/admin/geocoding/reference-data/import")]
    public async Task Import_MismatchedLocatorName_Returns400()
    {
        using var content = new MultipartFormDataContent();
        AddCsvPart(content, ReferenceCsv, "redlands.csv");
        content.Add(new StringContent("SomeOtherLocator"), "locatorName");

        using var response = await _adminClient.PostAsync(ImportRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not match the geocode service name", body, StringComparison.Ordinal);
    }

    private static MultipartFormDataContent BuildImportForm()
    {
        var content = new MultipartFormDataContent();
        AddCsvPart(content, ReferenceCsv, "redlands.csv");
        content.Add(new StringContent(LocatorName), "locatorName");
        return content;
    }

    private static void AddCsvPart(MultipartFormDataContent content, string csv, string fileName)
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(part, "referenceData", fileName);
    }
}
