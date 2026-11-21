// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Infrastructure.SecurityState;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Tests.Features.Infrastructure.SecurityState;

public sealed class DataProtectionJobSecurityContextSealerTests
{
    private const string Binding = "record-1";

    private static readonly JobSecurityContext Submitter = new(
        "reader-user", "tenant-a", [new JobSecurityClaim("role", "reader"), new JobSecurityClaim("region", "west")], "role");

    [UnitTest]
    public void Seal_VerifiesForTheSameBindingAndSnapshot()
    {
        var sealer = new DataProtectionJobSecurityContextSealer(new EphemeralDataProtectionProvider());

        sealer.Verify(Binding, Submitter, sealer.Seal(Binding, Submitter)).Should().BeTrue();
    }

    [UnitTest]
    public void Seal_DoesNotVerifyAnEditedSnapshot()
    {
        var sealer = new DataProtectionJobSecurityContextSealer(new EphemeralDataProtectionProvider());
        var seal = sealer.Seal(Binding, Submitter);

        var edited = Submitter with { Claims = [.. Submitter.Claims, new JobSecurityClaim("role", "admin")] };

        sealer.Verify(Binding, edited, seal).Should().BeFalse();
    }

    [UnitTest]
    public void Seal_DoesNotVerifyForAnotherRecord()
    {
        var sealer = new DataProtectionJobSecurityContextSealer(new EphemeralDataProtectionProvider());

        sealer.Verify("record-2", Submitter, sealer.Seal(Binding, Submitter)).Should().BeFalse();
    }

    [UnitTest]
    public void Seal_DoesNotVerifyWithAnotherKeyRing()
    {
        var seal = new DataProtectionJobSecurityContextSealer(new EphemeralDataProtectionProvider()).Seal(Binding, Submitter);

        new DataProtectionJobSecurityContextSealer(new EphemeralDataProtectionProvider())
            .Verify(Binding, Submitter, seal).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-seal")]
    [InlineData("dp1:not-protected")]
    public void Verify_RefusesAMissingOrMalformedSeal(string? seal)
    {
        var sealer = new DataProtectionJobSecurityContextSealer(new EphemeralDataProtectionProvider());

        sealer.Verify(Binding, Submitter, seal).Should().BeFalse();
    }
}
