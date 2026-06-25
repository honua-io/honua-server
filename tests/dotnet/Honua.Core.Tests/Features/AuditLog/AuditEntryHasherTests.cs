// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Tests.Features.AuditLog;

/// <summary>
/// Unit tests for the tamper-evident hash chain primitive (#350). The same
/// canonical hashing is used by the Postgres writer (chain construction) and the
/// integrity verifier (chain replay), so determinism and injectivity are the
/// load-bearing properties.
/// </summary>
public sealed class AuditEntryHasherTests
{
    private static string Hash(
        string? prev = null,
        string actor = "user-1",
        string action = "auth.success",
        string resourceType = "http",
        string? resourceId = "/api/v1/admin/x",
        string details = "{}")
        => AuditEntryHasher.ComputeEntryHash(
            prev,
            new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
            AuditEventType.Authentication,
            actor,
            AuditActorType.UserId,
            resourceType,
            resourceId,
            action,
            AuditOutcome.Success,
            "corr-1",
            "10.0.0.1",
            "agent/1.0",
            details);

    [Fact]
    public void ComputeEntryHash_IsDeterministic()
    {
        Hash().Should().Be(Hash());
    }

    [Fact]
    public void ComputeEntryHash_ReturnsLowercaseHex64()
    {
        var hash = Hash();
        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeEntryHash_ChangesWhenAnyFieldChanges()
    {
        var baseline = Hash();
        Hash(actor: "user-2").Should().NotBe(baseline, "actor is part of the canonical form");
        Hash(action: "auth.failure").Should().NotBe(baseline, "action is part of the canonical form");
        Hash(details: "{\"x\":1}").Should().NotBe(baseline, "details is part of the canonical form");
    }

    [Fact]
    public void ComputeEntryHash_ChangesWhenPreviousHashChanges()
    {
        // The chain link must influence the hash, otherwise reordering / deletion
        // would be undetectable.
        Hash(prev: null).Should().NotBe(Hash(prev: new string('a', 64)));
    }

    [Fact]
    public void ComputeEntryHash_NullResourceId_DiffersFromEmpty()
    {
        // The null sentinel and an empty string must not collide, so a row with a
        // missing field can't be forged into one with an empty field.
        Hash(resourceId: null).Should().NotBe(Hash(resourceId: string.Empty));
    }

    [Fact]
    public void ComputeEntryHash_IsInjective_AcrossFieldBoundaries()
    {
        // Length-prefixing must prevent field-boundary ambiguity: ("ab","c") and
        // ("a","bc") must hash differently even though naive concatenation collides.
        Hash(resourceType: "ab", action: "c").Should()
            .NotBe(Hash(resourceType: "a", action: "bc"));
    }
}
