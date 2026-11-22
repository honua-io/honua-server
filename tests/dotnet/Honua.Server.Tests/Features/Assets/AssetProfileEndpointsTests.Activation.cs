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
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task ActivateProfile_DuplicateOrNullAssetIds_RejectsWithoutAdvancingVersion()
    {
        var profileId = await CreateProfileAsync();
        await DuplicateAssetIdsAsync();
        var duplicate = await ActivateAsync(profileId);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssetTestData.ReadJsonAsync(duplicate))["errors"]!.AsArray()
            .Should().Contain(error => error!["field"]!.GetValue<string>() == "fields.assetId");

        await RemoveAssetIdsAsync();
        var missing = await ActivateAsync(profileId);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssetTestData.ReadJsonAsync(missing))["errors"]!.AsArray()
            .Should().Contain(error => error!["field"]!.GetValue<string>() == "fields.assetId");

        await RestoreAssetIdsAsync();
        var accepted = await ActivateAsync(profileId);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
        stored!.Version.Should().Be(2);
        stored.State.Should().Be(AssetProfileState.Active);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task ActivateProfile_ReadOnlyMappedField_ReturnsFieldError()
    {
        var profileId = await CreateProfileAsync();
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = "condition_score", Type = MetadataV2FieldType.Integer, Nullable = true, EditableValue = false });

        var response = await ActivateAsync(profileId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssetTestData.ReadJsonAsync(response))["errors"]!.AsArray()
            .Should().Contain(error => error!["field"]!.GetValue<string>() == "fields.condition");
    }

    private Task<HttpResponseMessage> ActivateAsync(Guid profileId)
        => _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}",
            new AssetProfileWriteRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 1, State = AssetProfileState.Active },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

    private Task RestoreAssetIdsAsync()
        => WriteAssetIdsAsync("attributes || jsonb_build_object('sign_id', objectid::text)");

    private Task DuplicateAssetIdsAsync()
        => WriteAssetIdsAsync("attributes || jsonb_build_object('sign_id', 'duplicate'::text)");

    private Task RemoveAssetIdsAsync() => WriteAssetIdsAsync("attributes - 'sign_id'");

    private async Task WriteAssetIdsAsync(string expression)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE features SET attributes = {expression} WHERE layer_id = @layer";
        command.Parameters.AddWithValue("layer", WebAppFixture.TestLayerId);
        (await command.ExecuteNonQueryAsync()).Should().BeGreaterThan(1, "the activation checks need multiple fixture assets");
    }
}
