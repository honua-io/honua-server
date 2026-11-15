// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

// The one holder rule the lease stores and the edit guard share.
[Protocol(TestProtocols.Infrastructure)]
public sealed class LockHolderTests
{
    private static readonly LockHolder Alice = new("alice", "Alice Editor", "session-a", "tenant-1");
    private static readonly LockHolder Owner = Alice with { PrincipalName = "alice@example.test" };

    [UnitTest]
    [Operation(Operations.Security)]
    public void Admits_LeaseWithPrincipal_RequiresThatPrincipal()
    {
        Owner.Admits(Owner with { DisplayName = "Renamed" }).Should().BeTrue();
        Owner.Admits(Owner with { PrincipalName = "other@example.test" }).Should().BeFalse();
        Owner.Admits(Owner with { PrincipalName = null }).Should().BeFalse();
        Owner.Admits(Owner with { SessionId = "session-b" }).Should().BeFalse();
        Owner.Admits(Owner with { HolderId = "bob" }).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void Admits_LeaseWithoutPrincipal_MatchesTheCallerSuppliedFields()
    {
        Alice.Admits(Alice with { PrincipalName = "alice@example.test" }).Should().BeTrue(
            "a lease taken outside an authenticated request is matched on the caller-supplied fields");
        Alice.Admits(Alice with { HolderId = "bob" }).Should().BeFalse();
        Alice.Admits(Alice with { SessionId = null }).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void Admits_BlankAndMissingSessionOrTenant_AreTheSame()
    {
        var bare = new LockHolder("alice");

        bare.Admits(bare with { SessionId = "", TenantId = " " }).Should().BeTrue();
        (bare with { TenantId = "" }).Admits(bare).Should().BeTrue();
        bare.Admits(bare with { TenantId = "tenant-1" }).Should().BeFalse();
        (bare with { SessionId = "" }).Admits(bare with { SessionId = "session-a" }).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void RenewedBy_KeepsTheLeasePrincipal()
    {
        Alice.RenewedBy(Alice with { DisplayName = "Renamed", PrincipalName = "bob@example.test" })
            .Should().Be(Alice with { DisplayName = "Renamed" });
        Owner.RenewedBy(Owner with { DisplayName = "Renamed" }).PrincipalName.Should().Be("alice@example.test");
    }
}
