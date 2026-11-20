// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Publishing.Content;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Publishing.Content;

[Protocol(ProtocolNames.TestQuality)]
public sealed class PublicLinkLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Fact]
    public void ALinkWithoutAnExpiry_GetsTheDefaultLifetime()
        => PublicLinkLifetime.Resolve(null, Now).Should().Be((Now + PublicLinkLifetime.Default, (string?)null));

    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Fact]
    public void ARequestedExpiry_IsKeptWithinTheMaximum()
    {
        var requested = Now.AddDays(90);

        PublicLinkLifetime.Resolve(requested, Now).Should().Be((requested, (string?)null));
        PublicLinkLifetime.Resolve(Now + PublicLinkLifetime.Maximum, Now).ExpiresAt.Should().Be(Now + PublicLinkLifetime.Maximum);
    }

    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Fact]
    public void PastAndOverlongExpiries_AreRefused()
    {
        PublicLinkLifetime.Resolve(Now, Now).Should().Be(((DateTimeOffset?)null, PublicLinkLifetime.FutureMessage));
        PublicLinkLifetime.Resolve(Now + PublicLinkLifetime.Maximum + TimeSpan.FromSeconds(1), Now)
            .Should().Be(((DateTimeOffset?)null, PublicLinkLifetime.MaximumMessage));
    }

    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Fact]
    public void AStoredLinkWithoutAnExpiry_EndsAtTheMaximumAfterCreation()
    {
        var created = Now.AddDays(-10);

        PublicLinkLifetime.HasEnded(null, created, Now).Should().BeFalse();
        PublicLinkLifetime.HasEnded(null, created, created + PublicLinkLifetime.Maximum).Should().BeTrue();
        PublicLinkLifetime.HasEnded(Now.AddMinutes(1), created, Now).Should().BeFalse();
        PublicLinkLifetime.HasEnded(Now, created, Now).Should().BeTrue();
    }
}
