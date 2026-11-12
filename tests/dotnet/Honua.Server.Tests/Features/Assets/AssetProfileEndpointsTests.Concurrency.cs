// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class AssetProfileEndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_ConcurrentReplay_CreatesOneProfileAndReceipt()
    {
        var request = ProfileRequest(Guid.NewGuid());
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            _fixture.Client.PostAsJsonAsync(ProfilesPath, request, AssetsJsonContext.Default.AssetProfileWriteRequest)));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Created);
        var bodies = await Task.WhenAll(responses.Select(AssetTestData.ReadJsonAsync));
        bodies.Select(body => body["profile"]!["profileId"]!.GetValue<string>()).Distinct().Should().ContainSingle();
        bodies.Count(body => body["outcome"]!.GetValue<string>() == AssetCommandOutcome.Accepted).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "asset_profiles")).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "asset_command_receipts")).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task UpdateProfile_ConcurrentExpectedVersion_OneEditWins()
    {
        var profileId = await CreateProfileAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}", new AssetProfileWriteRequest
            {
                CommandId = Guid.NewGuid(), ExpectedVersion = 1, Name = $"Edit {index}",
            }, AssetsJsonContext.Default.AssetProfileWriteRequest)));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(7);
        var stored = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
        stored!.Version.Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("GET /api/v1/assets/profiles")]
    [Endpoint("GET /api/v1/assets/profiles/{profileId}")]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task ProfileOperations_RestrictedLayer_ApplySharedAuthorization()
    {
        var profileId = await CreateProfileAsync();
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new AccessPolicy
        {
            AllowedWriteRoles = ["asset-specialist"],
        });

        var get = await _fixture.Client.GetAsync($"{ProfilesPath}/{profileId}");
        var list = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}?serviceId={WebAppFixture.TestServiceId}", AssetsJsonContext.Default.AssetProfileListResult);
        var update = await _fixture.Client.PutAsJsonAsync($"{ProfilesPath}/{profileId}", new AssetProfileWriteRequest
        {
            CommandId = Guid.NewGuid(), ExpectedVersion = 1, Name = "Denied edit",
        }, AssetsJsonContext.Default.AssetProfileWriteRequest);
        var create = await _fixture.Client.PostAsJsonAsync(ProfilesPath, ProfileRequest(Guid.NewGuid()), AssetsJsonContext.Default.AssetProfileWriteRequest);

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list!.Items.Should().BeEmpty();
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AssetTestData.CountAsync(_fixture, "asset_profiles")).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "asset_command_receipts")).Should().Be(1);
    }
}
