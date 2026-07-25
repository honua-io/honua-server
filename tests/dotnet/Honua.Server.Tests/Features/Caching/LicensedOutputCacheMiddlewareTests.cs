// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OutputCaching;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Verifies the composed middleware pipeline so endpoint and named cache policies
/// cannot re-enable output caching after the license decision.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class LicensedOutputCacheMiddlewareTests
{
    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("GET /rest/services")]
    public async Task NamedCachePolicy_CommunityBypassesStore_ProUsesStore()
    {
        var communityStore = Substitute.For<IOutputCacheStore>();
        await using (var community = CreateFixture(HonuaEdition.Community, communityStore))
        {
            await community.InitializeAsync();
            using var response = await community.Client.GetAsync("/rest/services?f=json");
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync();
        }

        await communityStore.DidNotReceiveWithAnyArgs()
            .GetAsync(default!, default);
        await communityStore.DidNotReceiveWithAnyArgs()
            .SetAsync(default!, default!, default!, default, default);

        var proStore = Substitute.For<IOutputCacheStore>();
        await using (var pro = CreateFixture(HonuaEdition.Pro, proStore))
        {
            await pro.InitializeAsync();
            using var response = await pro.Client.GetAsync("/rest/services?f=json");
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync();
        }

        await proStore.ReceivedWithAnyArgs()
            .GetAsync(default!, default);
        await proStore.ReceivedWithAnyArgs()
            .SetAsync(default!, default!, default!, default, default);
    }

    private static WebAppFixture CreateFixture(
        HonuaEdition edition,
        IOutputCacheStore cacheStore)
        => new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .WithTestLicense(edition)
            .ConfigureWebHost(builder =>
                builder.UseSetting("HONUA_DEV_AUTH", "false"))
            .ReplaceService(cacheStore);
}
