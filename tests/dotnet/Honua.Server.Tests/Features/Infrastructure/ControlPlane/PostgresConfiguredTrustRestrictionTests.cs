// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresConfiguredTrustRestrictionTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Bootstrap_ConfigurationRevisionChanges_PreservesAdminIssuerRestrictions()
    {
        await using var harness = await CreateHarnessAsync();
        var original = TrustOptions();
        original.Value.TrustProfiles[0].AcceptedIssuerThumbprints = ["AA", "BB"];
        original.Value.TrustProfiles[0].AcceptedIssuerSubjects = ["CN=A", "CN=B"];
        original.Value.TrustProfiles[0].CustomTrustAnchorCertificates = ["anchor-a", "anchor-b"];
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original);
        var profile = (await taskA.GetProfileAsync("partner"))!;
        await taskA.UpsertProfileAsync(profile with
        {
            AcceptedIssuerThumbprints = ["AA"],
            AcceptedIssuerSubjects = ["CN=A"],
            CustomTrustAnchorCertificates = ["anchor-a"]
        });
        original.Value.TrustProfilesRevision = 2;
        original.Value.TrustProfiles[0].DisplayName = "Updated";

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original).ListProfilesAsync();

        var seen = (await taskA.GetProfileAsync("partner"))!;
        seen.DisplayName.Should().Be("Updated");
        seen.AcceptedIssuerThumbprints.Should().Equal("AA");
        seen.AcceptedIssuerSubjects.Should().Equal("CN=A");
        seen.CustomTrustAnchorCertificates.Should().Equal("anchor-a");
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bootstrap_IncompatibleChainRestrictions_DoesNotFallBackToBroaderTrust(bool disjointAnchors)
    {
        await using var harness = await CreateHarnessAsync();
        using var certificate = ClientCertificateValidationTests.CreateCertificate("CN=Partner", "spiffe://partner/reader");
        var original = TrustOptions();
        original.Value.TrustProfiles[0].CustomTrustAnchorCertificates = ["anchor-a"];
        original.Value.TrustProfiles[0].AcceptedIssuerSubjects = [certificate.Issuer];
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original);
        await taskA.ListProfilesAsync();
        var monitor = new ClientCertificateValidationTests.TestOptionsMonitor<ClientCertificateAuthenticationOptions>(new()
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "production"
        });
        var validator = new ClientCertificateValidator(monitor, taskA);
        var baseline = await validator.ValidateAsync(certificate);
        baseline.Succeeded.Should().BeTrue(baseline.Detail);
        var changed = TrustOptions();
        changed.Value.TrustProfilesRevision = 2;
        changed.Value.TrustProfiles[0].AcceptedIssuerSubjects = [certificate.Issuer];
        if (disjointAnchors)
        {
            changed.Value.TrustProfiles[0].CustomTrustAnchorCertificates = ["anchor-b"];
        }
        else
        {
            changed.Value.TrustProfiles[0].AcceptedIssuerThumbprints = ["BB"];
            changed.Value.TrustProfiles[0].CustomTrustAnchorCertificates = ["anchor-a"];
        }

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed).ListProfilesAsync();

        var seen = (await taskA.GetProfileAsync("partner"))!;
        seen.AcceptedIssuerThumbprints.Should().BeEmpty();
        seen.CustomTrustAnchorCertificates.Should().BeEmpty();
        seen.AcceptedIssuerSubjects.Should().Equal(certificate.Issuer);
        (await validator.ValidateAsync(certificate)).Succeeded.Should().BeFalse();
        seen.Enabled.Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bootstrap_MappingValidityChanges_PreservesTighterBounds(bool adminIsTighter)
    {
        await using var harness = await CreateHarnessAsync();
        var original = TrustOptions();
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original);
        await taskA.ListProfilesAsync();
        var start = DateTimeOffset.Parse("2026-11-13T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var end = start.AddDays(1);
        var mapping = (await taskA.GetProfileAsync("partner"))!.PrincipalMappings.Single();
        await taskA.UpsertMappingAsync("partner", mapping with
        {
            NotBefore = adminIsTighter ? start : null,
            NotAfter = adminIsTighter ? end : null
        });
        var changed = TrustOptions();
        changed.Value.TrustProfilesRevision = 2;
        changed.Value.TrustProfiles[0].PrincipalMappings[0].NotBefore = adminIsTighter ? null : start;
        changed.Value.TrustProfiles[0].PrincipalMappings[0].NotAfter = adminIsTighter ? null : end;

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed).ListProfilesAsync();

        var seen = (await taskA.GetProfileAsync("partner"))!.PrincipalMappings.Single();
        seen.NotBefore.Should().Be(start);
        seen.NotAfter.Should().Be(end);
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bootstrap_ExistingRevocationChanges_EnabledOnEitherSideWins(bool configuredEnables)
    {
        await using var harness = await CreateHarnessAsync();
        var original = TrustOptions();
        original.Value.TrustProfiles[0].Revocations =
        [new() { RevocationId = "revoked", FingerprintSha256 = "AA", Enabled = !configuredEnables }];
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original);
        await taskA.ListProfilesAsync();
        original.Value.TrustProfilesRevision = 2;
        original.Value.TrustProfiles[0].Revocations[0].Enabled = configuredEnables;

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original).ListProfilesAsync();

        (await taskA.GetProfileAsync("partner"))!.Revocations.Single().Enabled.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Bootstrap_RevocationTargetChanges_PreservesBothTargetsWithoutDuplicates()
    {
        await using var harness = await CreateHarnessAsync();
        var original = TrustOptions();
        original.Value.TrustProfiles[0].Revocations = [new() { RevocationId = "revoked", FingerprintSha256 = "AA" }];
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original);
        await taskA.ListProfilesAsync();
        original.Value.TrustProfilesRevision = 2;
        original.Value.TrustProfiles[0].Revocations[0].FingerprintSha256 = "BB";
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original).ListProfilesAsync();
        original.Value.TrustProfilesRevision = 3;

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original).ListProfilesAsync();

        var seen = (await taskA.GetProfileAsync("partner"))!.Revocations;
        seen.Should().HaveCount(2).And.OnlyContain(entry => entry.Enabled);
        seen.Select(entry => entry.RevocationId).Should().OnlyHaveUniqueItems();
        seen.Select(entry => entry.FingerprintSha256).Should().BeEquivalentTo("AA", "BB");
    }

    private static IOptions<ClientCertificateAuthenticationOptions> TrustOptions() => Options.Create(new ClientCertificateAuthenticationOptions
    {
        TrustProfiles =
        [new()
        {
            ProfileId = "partner", EnvironmentId = "production", DisplayName = "Partner", AcceptedIssuerThumbprints = ["AA"],
            PrincipalMappings = [new()
            {
                MappingId = "reader", MatchType = ClientCertificateIdentityType.SanUri, MatchValue = "spiffe://partner/reader",
                PrincipalId = "partner", DisplayName = "Partner"
            }]
        }]
    });

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfiguredTrustRestrictionTests));
}
