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

public sealed partial class AssetProfileEndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_ArchivedStateOrSharedColumn_ReturnsFieldErrors()
    {
        var archived = ProfileRequest(Guid.NewGuid()) with { State = AssetProfileState.Archived };
        var shared = ProfileRequest(Guid.NewGuid()) with
        {
            Fields = new AssetProfileFieldMap { AssetId = "sign_id", LastInspected = "inspected_on", LastServiced = "INSPECTED_ON" },
        };

        var archivedResponse = await _fixture.Client.PostAsJsonAsync(ProfilesPath, archived, AssetsJsonContext.Default.AssetProfileWriteRequest);
        var sharedResponse = await _fixture.Client.PostAsJsonAsync(ProfilesPath, shared, AssetsJsonContext.Default.AssetProfileWriteRequest);

        archivedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(archivedResponse)).Should().BeEquivalentTo("state");
        sharedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(sharedResponse)).Should().BeEquivalentTo("fields");
        (await AssetTestData.CountAsync(_fixture, "asset_profiles")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task UpdateProfile_ArchivedProfile_CannotBeReopened()
    {
        var profileId = await CreateProfileAsync();
        var archive = await PutStateAsync(profileId, (AssetProfileState.Archived, 1));

        var reopen = await PutStateAsync(profileId, (AssetProfileState.Active, 2));
        var renameArchived = await _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}",
            new AssetProfileWriteRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 2, Name = "Renamed Archived" },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

        archive.StatusCode.Should().Be(HttpStatusCode.OK);
        reopen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(reopen)).Should().BeEquivalentTo("state");
        (await AssetTestData.ReadJsonAsync(reopen))["errors"]![0]!["message"]!.GetValue<string>()
            .Should().Be("An archived profile cannot be reopened. The layer keeps this profile.");
        renameArchived.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(renameArchived)).Should().BeEquivalentTo("state");
        var stored = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
        stored!.State.Should().Be(AssetProfileState.Archived);
        stored.Version.Should().Be(2);

        var replacement = await _fixture.Client.PostAsJsonAsync(ProfilesPath, ProfileRequest(Guid.NewGuid()), AssetsJsonContext.Default.AssetProfileWriteRequest);
        replacement.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task UpdateProfile_DraftCanCorrectAssetId_ActiveCannot()
    {
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, new MetadataV2Field { Name = "alt_id", Type = MetadataV2FieldType.String });
        var profileId = await CreateProfileAsync();

        var corrected = await PutAssetIdAsync(profileId, ("alt_id", 1));
        corrected.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AssetTestData.ReadJsonAsync(corrected))["profile"]!["fields"]!["assetId"]!.GetValue<string>().Should().Be("alt_id");
        (await PutAssetIdAsync(profileId, ("sign_id", 2))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutStateAsync(profileId, (AssetProfileState.Active, 3))).StatusCode.Should().Be(HttpStatusCode.OK);

        var backToDraft = await PutStateAsync(profileId, (AssetProfileState.Draft, 4));
        backToDraft.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(backToDraft)).Should().BeEquivalentTo("state");

        var blocked = await PutAssetIdAsync(profileId, ("alt_id", 4));
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(blocked)).Should().BeEquivalentTo("fields.assetId");
        (await AssetTestData.ReadJsonAsync(blocked))["errors"]![0]!["message"]!.GetValue<string>()
            .Should().Be("The asset ID field cannot change after the profile leaves draft.");
        var stored = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
        stored!.Fields.AssetId.Should().Be("sign_id");
        stored.State.Should().Be(AssetProfileState.Active);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_EmptyCommandIdRejected_AndWhitespaceFormIdNormalized()
    {
        var emptyCommand = await _fixture.Client.PostAsJsonAsync(ProfilesPath, ProfileRequest(Guid.Empty), AssetsJsonContext.Default.AssetProfileWriteRequest);
        emptyCommand.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorFieldsAsync(emptyCommand)).Should().BeEquivalentTo("commandId");

        var blankForm = ProfileRequest(Guid.NewGuid()) with { InspectionFormId = "   ", ServiceFormId = "" };
        var created = await _fixture.Client.PostAsJsonAsync(ProfilesPath, blankForm, AssetsJsonContext.Default.AssetProfileWriteRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await AssetTestData.ReadJsonAsync(created);
        json["profile"]!["inspectionFormId"].Should().BeNull();
        json["profile"]!["serviceFormId"].Should().BeNull();
    }

    private Task<HttpResponseMessage> PutAssetIdAsync(Guid profileId, (string AssetId, long ExpectedVersion) change)
        => _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}",
            new AssetProfileWriteRequest
            {
                CommandId = Guid.NewGuid(),
                ExpectedVersion = change.ExpectedVersion,
                Fields = new AssetProfileFieldMap { AssetId = change.AssetId },
            },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

    private Task<HttpResponseMessage> PutStateAsync(Guid profileId, (string State, long ExpectedVersion) change)
        => _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}",
            new AssetProfileWriteRequest { CommandId = Guid.NewGuid(), ExpectedVersion = change.ExpectedVersion, State = change.State },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

    private static async Task<IEnumerable<string>> ErrorFieldsAsync(HttpResponseMessage response)
        => (await AssetTestData.ReadJsonAsync(response))["errors"]!.AsArray().Select(error => error!["field"]!.GetValue<string>()).ToList();
}
