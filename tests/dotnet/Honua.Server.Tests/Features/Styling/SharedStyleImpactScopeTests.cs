// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Styling;

public sealed class SharedStyleImpactScopeTests
{
    [UnitTest]
    public async Task StoreConsumerIndex_ReadsOnlyTheCallersTenant()
    {
        var store = Substitute.For<IStudioPackageStore>();
        store.ListContentItemsAsync(Arg.Any<StudioContentItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new StudioContentItemListResult());
        store.ListDraftsAsync(Arg.Any<StudioPackageDraftQuery>(), Arg.Any<CancellationToken>())
            .Returns(new StudioPackageDraftListResult());
        var tenant = new TenantScopeFilter("tenant-a", IncludeUnassigned: false);

        await new StoreSharedStyleConsumerIndex(store).ListConsumersAsync(
            new SharedStyleConsumerQuery { StyleId = "roads", Limit = 10, Tenant = tenant });

        await store.Received(1).ListContentItemsAsync(
            Arg.Is<StudioContentItemQuery>(query => query.Tenant == tenant), Arg.Any<CancellationToken>());
        await store.Received(1).ListDraftsAsync(
            Arg.Is<StudioPackageDraftQuery>(query => query.Tenant == tenant), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void ImpactHash_IgnoresDraftContentButNotConsumerIdentity()
    {
        var baseline = SharedStyleIdentity.ImpactHash(Impact(Draft("draft-1", contentHash: "aaa")));

        SharedStyleIdentity.ImpactHash(Impact(Draft("draft-1", contentHash: "bbb"))).Should().Be(baseline);
        SharedStyleIdentity.ImpactHash(Impact(Draft("draft-2", contentHash: "aaa"))).Should().NotBe(baseline);
    }

    private static SharedStyleConsumerRow Draft(string draftId, string contentHash) => new()
    {
        Kind = "map",
        ItemId = "item-1",
        PackageKey = "county-map",
        ContentHash = contentHash,
        PublicationState = "draft",
        DraftId = draftId,
        Generation = 1,
        IsCurrent = true
    };

    private static SharedStyleImpact Impact(SharedStyleConsumerRow consumer) => new()
    {
        StyleId = "roads",
        StoredVersion = 3,
        ReportedVersion = 3,
        EntityTag = "\"v3-abc\"",
        ContentHash = "abc",
        ImpactHash = string.Empty,
        ImpactComplete = true,
        Associations = [],
        Consumers = [consumer]
    };
}
