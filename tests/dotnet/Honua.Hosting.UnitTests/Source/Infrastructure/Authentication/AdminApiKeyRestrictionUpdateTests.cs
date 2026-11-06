// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// In-memory proof that restriction replacement keeps the secret and can clear a list.
/// </summary>
public sealed class AdminApiKeyRestrictionUpdateTests
{
    [UnitTest]
    public async Task UpdateRestrictionsAsync_ReplacesLists_PreservesSecret_AndClearsWhenEmpty()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync(
            "viewer",
            ["admin:read"],
            expiresAt: null,
            createdBy: "test",
            new AdminApiKeyRestrictions(["https://app.example.com"], ["203.0.113.0/24"]),
            CancellationToken.None);

        var updated = await store.UpdateRestrictionsAsync(
            created.Record.Id,
            new AdminApiKeyRestrictions(["https://ops.example.com"], null),
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.KeyHash.Should().Equal(created.Record.KeyHash);
        updated.KeyPrefix.Should().Be(created.Record.KeyPrefix);
        updated.AllowedReferrers.Should().Equal("https://ops.example.com");
        updated.AllowedIpRanges.Should().BeNull();
        (await store.ValidateAsync(created.Key, CancellationToken.None))!.Record.AllowedReferrers
            .Should().Equal("https://ops.example.com");

        var cleared = await store.UpdateRestrictionsAsync(
            created.Record.Id,
            new AdminApiKeyRestrictions([], []),
            CancellationToken.None);
        cleared!.AllowedReferrers.Should().BeNull();
        cleared.AllowedIpRanges.Should().BeNull();
        cleared.KeyHash.Should().Equal(created.Record.KeyHash);

        (await store.UpdateRestrictionsAsync(
            Guid.NewGuid(),
            new AdminApiKeyRestrictions(null, null),
            CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task UpdateRestrictionsAsync_ApprovedOperationCredential_IsLeftUnchanged()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync(
            "approved",
            AdminApiKeyPermission.CreateApprovedOperationGrants("POST", "/api/v1/admin/api-keys", "public"),
            expiresAt: null,
            createdBy: "test",
            CancellationToken.None);

        var updated = await store.UpdateRestrictionsAsync(
            created.Record.Id,
            new AdminApiKeyRestrictions(["https://app.example.com"], null),
            CancellationToken.None);

        updated.Should().BeNull();
        (await store.GetAsync(created.Record.Id, CancellationToken.None))!.AllowedReferrers.Should().BeNull();
    }
}
