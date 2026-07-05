// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Linq;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Tests.Features.Capabilities;

/// <summary>
/// Unit coverage for the #2335 (B3) manifest derivation seam: the
/// <c>honua.capability_manifest.v1</c> surface's <c>Capabilities[]</c> and
/// <c>Packages.Families[]</c> are derived by iterating the unified capability registry
/// through <see cref="CapabilityGateResolver"/>. These tests pin the registry roster
/// the manifest projects and the gate-resolver omit mechanism the manifest applies —
/// the Server-side wire equivalence is proven by the manifest endpoint golden test.
/// </summary>
public sealed class CapabilityManifestRegistryProjectionTests
{
    private static readonly CapabilityRegistry Registry = new CapabilityRegistry();

    // The frozen #1186 manifest roster, in the exact order the manifest serializes it.
    // Mirrors CapabilityManifestService's hand-curated list; the registry-derived path
    // must reproduce this order and set.
    private static readonly string[] ExpectedManifestCapabilityIds =
    [
        "package.metadata-v2",
        "package.release-package",
        "package.gitops-manifest",
        "package.map",
        "package.app",
        "temporal.filtering",
        "temporal.extent-discovery",
        "temporal.histogram",
        "temporal.time-series-tiles",
        "sync.offline",
        "realtime.feature-streams",
        "alerts.geofence",
        "jobs.runner",
        "ai.spec-apply",
        "ai.grounding",
        "ai.workflow-generation",
        "gitops.release-manifest",
        "transport.grpc",
        "transport.grpc-web",
        "transport.native-grpc",
        "transport.mcp",
        "transport.qgis",
        "security.mtls",
        "preview.file-import",
        "query.features",
        "analysis.spatial",
        "publication.metadata-release",
        "upload.file",
        "edit.features",
        "versioning.branch",
    ];

    private static bool IsManifestCapability(CapabilityDescriptor descriptor)
        => !descriptor.Id.StartsWith(CapabilityRegistry.McpToolIdPrefix, StringComparison.Ordinal)
            && !descriptor.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal)
            && !descriptor.Id.StartsWith(CapabilityRegistry.DataFormatIdPrefix, StringComparison.Ordinal);

    [Fact]
    public void Registry_ManifestDescriptors_MatchFrozenRosterInOrder()
    {
        var manifestIds = Registry.All
            .Where(IsManifestCapability)
            .Select(descriptor => descriptor.Id)
            .ToArray();

        manifestIds.Should().Equal(ExpectedManifestCapabilityIds);
    }

    // T10 (#2346): the built-experimental manifest capabilities flipped to
    // Experimental. With the default (flag-off) context these resolve
    // experimental-disabled, so the registry-derived manifest omits them.
    private static readonly string[] ExperimentalManifestCapabilityIds =
    [
        "temporal.filtering",
        "temporal.extent-discovery",
        "temporal.histogram",
        "temporal.time-series-tiles",
        "sync.offline",
        "realtime.feature-streams",
        // alerts.geofence promoted to Implemented (GA) in #2427 — no longer omitted.
        "security.mtls",
        // versioning.branch (VMS REST surface) gated Preview in the BH6-001/BH6-002 fix batch.
        "versioning.branch",
    ];

    [Fact]
    public void Registry_ExperimentalManifestDescriptors_AreOmittedWhenFlagOff()
    {
        // T10 flip: each built-experimental manifest descriptor is Experimental and,
        // with flags off (default), resolves experimental-disabled so the manifest
        // omits it. Customers opt in per capability via Capabilities:Experimental.
        var context = CapabilityGateContext.Default;

        foreach (var id in ExperimentalManifestCapabilityIds)
        {
            var descriptor = Registry.Find(id);
            descriptor.Should().NotBeNull($"{id} is a manifest capability");
            descriptor!.Maturity.Should().Be(CapabilityMaturity.Experimental);

            var resolution = CapabilityGateResolver.Resolve(descriptor, context);
            resolution.Enabled.Should().BeFalse($"{id} is experimental and flag-off");
            resolution.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
        }
    }

    [Fact]
    public void Registry_InReleaseManifestDescriptors_StayImplemented_AndAreNeverOmitted()
    {
        // Every manifest descriptor NOT in the built-experimental flip set stays
        // Implemented and resolves enabled, so the in-release manifest surface is
        // unaffected by T10.
        var context = CapabilityGateContext.Default;
        var experimental = ExperimentalManifestCapabilityIds.ToHashSet(StringComparer.Ordinal);

        foreach (var descriptor in Registry.All.Where(IsManifestCapability))
        {
            if (experimental.Contains(descriptor.Id))
            {
                continue;
            }

            descriptor.Maturity.Should().Be(
                CapabilityMaturity.Implemented,
                $"{descriptor.Id} ships in the first release");

            var resolution = CapabilityGateResolver.Resolve(descriptor, context);
            resolution.Enabled.Should().BeTrue($"{descriptor.Id} must resolve enabled while Implemented");
            resolution.ReasonCode.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("package.metadata-v2", MetadataV2Constants.SchemaVersion)]
    [InlineData("package.release-package", MetadataV2Constants.ApiVersion)]
    [InlineData("package.gitops-manifest", MetadataV2Constants.ApiVersion)]
    [InlineData("package.map", "honua_map_package.v1")]
    [InlineData("package.app", "honua_app_package.v1")]
    public void Registry_PackageDescriptors_CarryPackageSchemaVersion(string id, string expectedSchemaVersion)
    {
        // The null seam B1 left is now populated from the package families so the
        // derived Packages.Families[] and the descriptors cannot diverge.
        var descriptor = Registry.Find(id);

        descriptor.Should().NotBeNull();
        descriptor!.Category.Should().Be("packages");
        descriptor.PackageSchemaVersion.Should().Be(expectedSchemaVersion);
    }

    [Fact]
    public void Registry_NonPackageManifestDescriptors_HaveNullPackageSchemaVersion()
    {
        foreach (var descriptor in Registry.All.Where(IsManifestCapability))
        {
            if (descriptor.Category == "packages")
            {
                continue;
            }

            descriptor.PackageSchemaVersion.Should().BeNull($"{descriptor.Id} is not a package family");
        }
    }

    [Fact]
    public void GateResolver_OmitMechanism_PresentWhenImplemented_AbsentWhenExperimentalFlagOff()
    {
        // The manifest omits a descriptor exactly when the gate resolves it
        // experimental-and-flag-off. Prove the two branches the projection keys on.
        var implemented = Descriptor(CapabilityMaturity.Implemented);
        var experimental = Descriptor(CapabilityMaturity.Experimental);

        var flagsOff = CapabilityGateContext.Default;

        CapabilityGateResolver.Resolve(implemented, flagsOff).Enabled.Should().BeTrue();

        var experimentalOff = CapabilityGateResolver.Resolve(experimental, flagsOff);
        experimentalOff.Enabled.Should().BeFalse();
        experimentalOff.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void GateResolver_OmitMechanism_PresentWhenExperimentalFlagOn()
    {
        var experimental = Descriptor(CapabilityMaturity.Experimental);
        var flags = new CapabilityFlagOptions { Enabled = true };
        var context = new CapabilityGateContext { ExperimentalFlags = flags };

        var resolution = CapabilityGateResolver.Resolve(experimental, context);

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    private static CapabilityDescriptor Descriptor(CapabilityMaturity maturity) => new()
    {
        Id = "temporal.filtering",
        Category = "temporal",
        Kind = CapabilityKind.Feature,
        Maturity = maturity,
    };
}
