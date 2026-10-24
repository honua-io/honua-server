// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Admin;

/// <summary>
/// Endpoint pin and external-schema scope for a committed PostGIS sink (#6399, #6400).
/// </summary>
public sealed class CommittedSinkPublicationTests
{
    private const string Connection =
        "Host=db.example;Port=5432;Database=gis;Username=loader;Password=secret";

    [UnitTest]
    public void Fingerprint_UsesHostPortAndDatabaseOnly()
    {
        var first = ConnectionEndpointFingerprint.TryCompute(Connection);
        var rotated = ConnectionEndpointFingerprint.TryCompute(
            "Server=DB.Example;Database=gis;Username=other;Password=changed");

        first.Should().NotBeNull();
        first.Should().HaveLength(64);
        ConnectionEndpointFingerprint.IsWellFormed(first).Should().BeTrue();
        rotated.Should().Be(first);
        first.Should().NotContain("secret");
        first.Should().NotContain("db.example");
        ConnectionEndpointFingerprint.TryCompute(
            "Server=DB.Example;Database=GIS;Username=other;Password=changed")
            .Should().NotBe(first);
        ConnectionEndpointFingerprint.Matches(
            "Server=DB.Example;Database=GIS;Username=other;Password=changed", first)
            .Should().BeFalse();
    }

    [UnitTest]
    public void Fingerprint_RejectsAMovedEndpoint()
    {
        var pinned = ConnectionEndpointFingerprint.TryCompute(Connection);

        ConnectionEndpointFingerprint.Matches(Connection, pinned).Should().BeTrue();
        ConnectionEndpointFingerprint.Matches(
            "Host=other.example;Port=5432;Database=gis;Password=secret", pinned).Should().BeFalse();
        ConnectionEndpointFingerprint.Matches("not a connection string", pinned).Should().BeFalse();
        ConnectionEndpointFingerprint.Matches(Connection, "abcd").Should().BeFalse();
        ConnectionEndpointFingerprint.TryCompute(null).Should().BeNull();
    }

    [UnitTest]
    public void ExternalSchema_IsAllowedOnlyInsideTheScope()
    {
        CommittedExternalSchemaPublication.AllowsUnlisted("results", isReserved: false, isValidIdentifier: true)
            .Should().BeFalse();

        using (CommittedExternalSchemaPublication.Begin())
        {
            CommittedExternalSchemaPublication.IsActive.Should().BeTrue();
            CommittedExternalSchemaPublication.AllowsUnlisted("results", false, true).Should().BeTrue();
            CommittedExternalSchemaPublication.AllowsUnlisted("pg_catalog", true, true).Should().BeFalse();
            CommittedExternalSchemaPublication.AllowsUnlisted("results", false, false).Should().BeFalse();

            using (CommittedExternalSchemaPublication.Begin())
            {
                CommittedExternalSchemaPublication.AllowsUnlisted("results", false, true).Should().BeTrue();
            }

            CommittedExternalSchemaPublication.IsActive.Should().BeTrue();
        }

        CommittedExternalSchemaPublication.IsActive.Should().BeFalse();
        CommittedExternalSchemaPublication.AllowsUnlisted("results", false, true).Should().BeFalse();
    }
}
