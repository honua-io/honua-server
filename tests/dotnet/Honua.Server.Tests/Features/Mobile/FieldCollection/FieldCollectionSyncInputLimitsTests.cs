// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Mobile.FieldCollection;

// Input limits for the FieldCollection sync contract: identifiers that the store
// keys on must be rejected with 400 before they reach PostgreSQL, never as a 5xx.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Features)]
public sealed class FieldCollectionSyncInputLimitsTests : IAsyncLifetime
{
    private const string SyncCursorPath = "/api/v1/fieldcollection/sync-cursor";
    private const string ChangesPath = "/api/v1/fieldcollection/changes";
    private const string ClientIdHeader = "X-Honua-Client-Id";
    private const int MaxIdentifierLength = 256;
    private const int MaxClientIdLength = 128;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("changeId", MaxIdentifierLength + 1)]
    [InlineData("changeId", 8_192)]
    [InlineData("featureId", MaxIdentifierLength + 1)]
    [InlineData("featureId", 8_192)]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_OversizedIdentifier_Returns400(string field, int length)
    {
        // Random text does not compress, so 8 KiB exceeds the PostgreSQL B-tree row limit
        // on the (client_id, change_id) and (feature_id, layer_id) primary keys.
        var oversized = IncompressibleText(length);
        var response = await PushInsertAsync(
            changeId: field == "changeId" ? oversized : Guid.NewGuid().ToString("N"),
            featureId: field == "featureId" ? oversized : $"feat-{Guid.NewGuid():N}");

        await ShouldBeProblemAsync(response, $"Field '{field}' must be at most");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_IdentifiersAtLimit_AreApplied()
    {
        var response = await PushInsertAsync(
            changeId: IncompressibleText(MaxIdentifierLength),
            featureId: IncompressibleText(MaxIdentifierLength));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
    }

    [IntegrationTheory]
    [InlineData("changeId")]
    [InlineData("featureId")]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_IdentifierWithNulCharacter_Returns400(string field)
    {
        var response = await PushInsertAsync(
            changeId: field == "changeId" ? "change\0id" : Guid.NewGuid().ToString("N"),
            featureId: field == "featureId" ? "feature\0id" : $"feat-{Guid.NewGuid():N}");

        await ShouldBeProblemAsync(response, $"Field '{field}' must not contain NUL");
    }

    [IntegrationTheory]
    [InlineData("""{"type":"Feature","geometry":null,"properties":{"name":"a\u0000b"}}""")]
    [InlineData("""{"type":"Feature","geometry":null,"properties":{"na\u0000me":"value"}}""")]
    [InlineData("""{"type":"Feature","geometry":null,"properties":{"tags":["ok","bad\u0000"]}}""")]
    [InlineData("""{"type":"Feature","geometry":null,"properties":{"name":"lone \ud800 surrogate"}}""")]
    [InlineData("""{"type":"Feature","geometry":null,"properties":{"\udc00":"lone low surrogate"}}""")]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_FeaturePayloadWithNulCharacter_Returns400(string feature)
    {
        var response = await PushRawAsync(Guid.NewGuid().ToString("N"), $"feat-{Guid.NewGuid():N}", feature);

        await ShouldBeProblemAsync(response, "Field 'feature'");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_IdentifierWithUnpairedSurrogateEscape_Returns400()
    {
        var body = $$"""
            {"changeId":"change\ud800id","featureId":"feat-{{Guid.NewGuid():N}}","layerId":180,"operation":"insert","feature":{"type":"Feature","geometry":null,"properties":{}}}
            """;

        var response = await _client.PostAsync(ChangesPath, new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_FeaturePayloadWithEscapedBackslashText_IsApplied()
    {
        // "\\u0000" is a backslash followed by the text u0000, not a NUL character.
        var response = await PushRawAsync(
            Guid.NewGuid().ToString("N"),
            $"feat-{Guid.NewGuid():N}",
            """{"type":"Feature","geometry":null,"properties":{"path":"C:\\u0000"}}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/sync-cursor")]
    public async Task SyncCursor_OversizedClientId_Returns400InsteadOfSharingATruncatedCursor()
    {
        // Two devices whose ids share the first 128 characters must not share one cursor.
        var sharedPrefix = new string('d', MaxClientIdLength);
        using var request = new HttpRequestMessage(HttpMethod.Get, SyncCursorPath);
        request.Headers.Add(ClientIdHeader, sharedPrefix + "-device-a");

        var response = await _client.SendAsync(request);

        await ShouldBeProblemAsync(response, ClientIdHeader);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_OversizedClientId_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChangesPath)
        {
            Content = InsertContent(Guid.NewGuid().ToString("N"), $"feat-{Guid.NewGuid():N}"),
        };
        request.Headers.Add(ClientIdHeader, new string('c', MaxClientIdLength + 1));

        var response = await _client.SendAsync(request);

        await ShouldBeProblemAsync(response, ClientIdHeader);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/sync-cursor")]
    public async Task SyncCursor_ClientIdAtLimit_IsEchoedUnchanged()
    {
        var clientId = new string('e', MaxClientIdLength);
        using var request = new HttpRequestMessage(HttpMethod.Get, SyncCursorPath);
        request.Headers.Add(ClientIdHeader, clientId);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("clientId").GetString().Should().Be(clientId);
    }

    private Task<HttpResponseMessage> PushInsertAsync(string changeId, string featureId)
        => _client.PostAsync(ChangesPath, InsertContent(changeId, featureId));

    private Task<HttpResponseMessage> PushRawAsync(string changeId, string featureId, string featureJson)
    {
        var body = $$"""
            {"changeId":{{JsonSerializer.Serialize(changeId)}},"featureId":{{JsonSerializer.Serialize(featureId)}},"layerId":180,"operation":"insert","feature":{{featureJson}}}
            """;
        return _client.PostAsync(ChangesPath, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static JsonContent InsertContent(string changeId, string featureId) => JsonContent.Create(new
    {
        changeId,
        featureId,
        layerId = 180,
        operation = "insert",
        timestamp = DateTimeOffset.UtcNow,
        feature = new
        {
            type = "Feature",
            geometry = new { type = "Point", coordinates = new[] { 0.5, 0.5 } },
            properties = new { name = "input limits" },
        },
    });

    private static string IncompressibleText(int length)
    {
        var text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));
        return text[..length];
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, string expectedSubject)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        // The serializer may escape quotes, so compare the decoded problem detail.
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("detail").GetString().Should().Contain(expectedSubject);
    }
}
