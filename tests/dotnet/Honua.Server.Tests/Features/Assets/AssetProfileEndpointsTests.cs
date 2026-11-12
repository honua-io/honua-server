// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AssetProfileEndpointsTests : IAsyncLifetime
{
    private const string ProfilesPath = "/api/v1/assets/profiles";

    private static readonly string[] Labels = ["Very poor", "Poor", "Fair", "Good", "Very good"];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, new MetadataV2Field { Name = "sign_id", Type = MetadataV2FieldType.String });
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, new MetadataV2Field { Name = "condition_score", Type = MetadataV2FieldType.Integer, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, new MetadataV2Field { Name = "inspected_on", Type = MetadataV2FieldType.DateTime, Nullable = true });
        await AssetTestData.ResetAsync(_fixture);
    }

    public async Task DisposeAsync()
    {
        await AssetTestData.ResetAsync(_fixture);
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_ReplayAndReuse_ReturnsStoredReceiptOrConflict()
    {
        var commandId = Guid.NewGuid();
        var request = ProfileRequest(commandId);

        var created = await _fixture.Client.PostAsJsonAsync(ProfilesPath, request, AssetsJsonContext.Default.AssetProfileWriteRequest);
        var replay = await _fixture.Client.PostAsJsonAsync(ProfilesPath, request, AssetsJsonContext.Default.AssetProfileWriteRequest);
        var reused = await _fixture.Client.PostAsJsonAsync(ProfilesPath, request with { Name = "Other" }, AssetsJsonContext.Default.AssetProfileWriteRequest);
        var otherSpelling = await _fixture.Client.PostAsJsonAsync(
            ProfilesPath,
            request with { ServiceId = WebAppFixture.TestServiceId.ToUpperInvariant() },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.CacheControl!.NoStore.Should().BeTrue();
        var first = await AssetTestData.ReadJsonAsync(created);
        first["outcome"]!.GetValue<string>().Should().Be(AssetCommandOutcome.Accepted);
        first["profile"]!["version"]!.GetValue<long>().Should().Be(1);
        first["profile"]!["fields"]!["assetId"]!.GetValue<string>().Should().Be("sign_id");

        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await AssetTestData.ReadJsonAsync(replay);
        second["outcome"]!.GetValue<string>().Should().Be(AssetCommandOutcome.Duplicate);
        second["profile"]!["profileId"]!.GetValue<string>().Should().Be(first["profile"]!["profileId"]!.GetValue<string>());

        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AssetTestData.ReadJsonAsync(otherSpelling))["outcome"]!.GetValue<string>().Should().Be(AssetCommandOutcome.Duplicate,
            "a service named in another case is the same service, so the command is a replay");
        (await AssetTestData.CountAsync(_fixture, "asset_profiles")).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_UnknownOrMistypedFields_ReturnsFieldErrors()
    {
        var request = ProfileRequest(Guid.NewGuid()) with
        {
            Fields = new AssetProfileFieldMap { AssetId = "no_such_column", Condition = "sign_id" },
            ConditionLabels = ["Poor", "Good"],
        };

        var response = await _fixture.Client.PostAsJsonAsync(ProfilesPath, request, AssetsJsonContext.Default.AssetProfileWriteRequest);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await AssetTestData.ReadJsonAsync(response);
        body["errors"]!.AsArray().Select(error => error!["field"]!.GetValue<string>())
            .Should().BeEquivalentTo("conditionLabels");

        var fieldErrors = await _fixture.Client.PostAsJsonAsync(
            ProfilesPath,
            request with { ConditionLabels = Labels },
            AssetsJsonContext.Default.AssetProfileWriteRequest);
        (await AssetTestData.ReadJsonAsync(fieldErrors))["errors"]!.AsArray().Select(error => error!["field"]!.GetValue<string>())
            .Should().BeEquivalentTo("fields.assetId", "fields.condition");
        (await AssetTestData.CountAsync(_fixture, "asset_profiles")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /api/v1/assets/profiles")]
    [Endpoint("GET /api/v1/assets/profiles/{profileId}")]
    public async Task GetAndListProfiles_ReturnTheStoredProfile()
    {
        var profileId = await CreateProfileAsync();

        var get = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
        var list = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}?serviceId={WebAppFixture.TestServiceId}", AssetsJsonContext.Default.AssetProfileListResult);
        var missing = await _fixture.Client.GetAsync($"{ProfilesPath}/{Guid.NewGuid()}");
        var unscoped = await _fixture.Client.GetAsync(ProfilesPath);

        get!.ConditionLabels.Should().Equal(Labels);
        list!.Items.Should().ContainSingle(item => item.ProfileId == profileId);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unscoped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task UpdateProfile_StaleExpectedVersion_ReturnsConflictWithCurrentVersion()
    {
        var profileId = await CreateProfileAsync();
        var update = new AssetProfileWriteRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedVersion = 1,
            State = AssetProfileState.Active,
            Fields = new AssetProfileFieldMap { Condition = "condition_score" },
        };

        var accepted = await _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}", update, AssetsJsonContext.Default.AssetProfileWriteRequest);
        var stale = await _fixture.Client.PutAsJsonAsync(
            $"{ProfilesPath}/{profileId}",
            update with { CommandId = Guid.NewGuid(), Name = "Late edit" },
            AssetsJsonContext.Default.AssetProfileWriteRequest);
        var moved = await _fixture.Client.PutAsJsonAsync(
            $"{ProfilesPath}/{profileId}",
            update with { CommandId = Guid.NewGuid(), ExpectedVersion = 2, LayerId = 7 },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AssetTestData.ReadJsonAsync(accepted))["profile"]!["version"]!.GetValue<long>().Should().Be(2);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AssetTestData.ReadJsonAsync(stale))["currentVersion"]!.GetValue<long>().Should().Be(2);
        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var stored = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
        stored!.Name.Should().Be("Signs");
        stored.State.Should().Be(AssetProfileState.Active);
        stored.Fields.Should().Be(new AssetProfileFieldMap { AssetId = "sign_id", Condition = "condition_score", LastInspected = "inspected_on" },
            "omitted mappings keep their stored values field by field");
    }

    private async Task<Guid> CreateProfileAsync()
    {
        var response = await _fixture.Client.PostAsJsonAsync(ProfilesPath, ProfileRequest(Guid.NewGuid()), AssetsJsonContext.Default.AssetProfileWriteRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return Guid.Parse((await AssetTestData.ReadJsonAsync(response))["profile"]!["profileId"]!.GetValue<string>());
    }

    private static AssetProfileWriteRequest ProfileRequest(Guid commandId) => new()
    {
        CommandId = commandId,
        ServiceId = WebAppFixture.TestServiceId,
        LayerId = WebAppFixture.TestLayerId,
        Name = "Signs",
        Fields = new AssetProfileFieldMap { AssetId = "SIGN_ID", Condition = "condition_score", LastInspected = "inspected_on" },
        ConditionLabels = Labels,
    };

}
