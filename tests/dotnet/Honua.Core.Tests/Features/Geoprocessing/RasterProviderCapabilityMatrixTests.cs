// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterProviderCapabilityMatrixTests
{
    [UnitTest]
    public void Discover_ServingPrimitiveWithoutExecutorOrProof_FailsClosedWithBothReasons()
    {
        var discovery = Discover(Row(), executors: [], proofs: []).Single();

        discovery.ServingPrimitiveStatus.Should().Be(RasterServingPrimitiveStatus.HonuaServingPath);
        discovery.HasDurableReferenceOutputExecutor.Should().BeFalse();
        discovery.HasProviderProof.Should().BeFalse();
        discovery.Capability.Availability.Should().Be(RasterProviderAvailability.Unavailable);
        discovery.Rejections.Select(rejection => rejection.Code).Should().Equal(
            RasterProviderCapabilityRejectionCodes.DurableReferenceExecutorMissing,
            RasterProviderCapabilityRejectionCodes.ProviderProofMissing);
        discovery.Capability.UnavailabilityReason.Should().Be(
            "durable_reference_executor_missing: No registered durable reference-output executor "
            + "declares postgis/raster.clip@1.0.0 variant 'pixel-center'.; "
            + "provider_proof_missing: No passing provider proof covers fixture "
            + "'clip.pixel-center-boundary.v1' for postgis/raster.clip@1.0.0 variant 'pixel-center' "
            + "on runtime 3.4.0.");
    }

    [UnitTest]
    public void Discover_ActualExecutorWithoutProof_StaysUnavailable()
    {
        var row = Row();
        var executor = new FakeExecutor(AvailableCapability(row));
        var registration = Registration(row, executor);

        var discovery = Discover(row, [registration], proofs: []).Single();

        discovery.HasDurableReferenceOutputExecutor.Should().BeTrue();
        discovery.HasProviderProof.Should().BeFalse();
        discovery.Rejections.Should().ContainSingle()
            .Which.Code.Should().Be(RasterProviderCapabilityRejectionCodes.ProviderProofMissing);
        discovery.Capability.Availability.Should().Be(RasterProviderAvailability.Unavailable);
    }

    [UnitTest]
    public void Discover_ExactReferenceExecutorAndProof_MakesVariantAvailable()
    {
        var row = Row();
        var executor = new FakeExecutor(AvailableCapability(row));

        var discovery = Discover(
            row,
            [Registration(row, executor)],
            [Proof(row)]).Single();

        discovery.HasDurableReferenceOutputExecutor.Should().BeTrue();
        discovery.HasProviderProof.Should().BeTrue();
        discovery.Rejections.Should().BeEmpty();
        discovery.Capability.Availability.Should().Be(RasterProviderAvailability.Available);
        discovery.Capability.UnavailabilityReason.Should().BeNull();
    }

    [UnitTest]
    public void Discover_RuntimeAndExtensionMismatch_AreBoundedDiscoveryMetadata()
    {
        var row = Row();
        var executor = new FakeExecutor(AvailableCapability(row));
        var runtime = new RasterProviderRuntimeSnapshot
        {
            ProviderId = "postgis",
            Engine = RasterEngine.Postgis,
            RuntimeVersion = "3.3.7",
            Extensions = [],
        };

        var discovery = RasterProviderCapabilityMatrix.Discover(
            [row],
            [runtime],
            [Registration(row, executor)],
            [Proof(row) with { RuntimeVersion = "3.3.7" }]).Single();

        discovery.Rejections.Select(rejection => rejection.Code).Should().Equal(
            RasterProviderCapabilityRejectionCodes.ProviderRuntimeBelowMinimum,
            RasterProviderCapabilityRejectionCodes.ProviderExtensionMissing);
        discovery.Rejections[0].Reason.Should().Be(
            "PostGIS runtime 3.3.7 is below the minimum supported version 3.4.0 for "
            + "postgis/raster.clip@1.0.0 variant 'pixel-center'.");
        discovery.Rejections[1].Reason.Should().Be(
            "Required provider extension 'postgis_raster' at version 3.4.0 or later was not discovered.");
    }

    [UnitTest]
    public void Discover_RegistrationNotDeclaredByActualExecutor_IsRejected()
    {
        var row = Row();
        var executor = new FakeExecutor();
        var registration = Registration(row, executor);

        var act = () => Discover(row, [registration], [Proof(row)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be declared by its actual IRasterProviderExecutor*");
    }

    [UnitTest]
    public void ProjectOperations_OneUnprovenVariant_KeepsWholeProcessUnavailable()
    {
        var proven = Row();
        var unproven = Row() with { SemanticVariantId = "default" };
        var executor = new FakeExecutor(AvailableCapability(proven));
        var discoveries = RasterProviderCapabilityMatrix.Discover(
            [proven, unproven],
            [Runtime()],
            [Registration(proven, executor), Registration(unproven, executor)],
            [Proof(proven)]);

        var projected = RasterProviderCapabilityMatrix.ProjectOperations(discoveries);

        projected.Should().ContainSingle();
        projected[0].Availability.Should().Be(RasterProviderAvailability.Unavailable);
        projected[0].UnavailabilityReason.Should().Contain(
            "semantic variant 'default' [provider_proof_missing]");
    }

    private static IReadOnlyList<RasterProviderCapabilityDiscovery> Discover(
        RasterProviderOperationCapabilityRow row,
        IReadOnlyList<RasterProviderExecutableSemanticVariant> executors,
        IReadOnlyList<RasterProviderSemanticProof> proofs) =>
        RasterProviderCapabilityMatrix.Discover([row], [Runtime()], executors, proofs);

    private static RasterProviderOperationCapabilityRow Row() => new()
    {
        ProviderId = "postgis",
        Engine = RasterEngine.Postgis,
        ProcessId = "raster.clip",
        SemanticVersion = "1.0.0",
        SemanticVariantId = "pixel-center",
        ImplementationVersion = "honua.postgis.raster.clip@1.0.0",
        PolicyVersion = "postgis-raster-v1",
        ServingPrimitiveStatus = RasterServingPrimitiveStatus.HonuaServingPath,
        ServingPrimitives = ["ST_Clip"],
        ServingPrimitiveNotes = "Serving clip exists; durable materialization does not.",
        MinimumRuntimeVersion = "3.4.0",
        RequiredExtensions =
        [
            new RasterProviderExtensionRequirement
            {
                ExtensionName = "postgis_raster",
                MinimumVersion = "3.4.0",
            },
        ],
        RequiredFixtureIds = ["clip.pixel-center-boundary.v1"],
    };

    private static RasterProviderRuntimeSnapshot Runtime() => new()
    {
        ProviderId = "postgis",
        Engine = RasterEngine.Postgis,
        RuntimeVersion = "3.4.0",
        Extensions =
        [
            new RasterProviderExtensionSnapshot
            {
                ExtensionName = "postgis_raster",
                Version = "3.4.0",
            },
        ],
    };

    private static RasterProviderCapability AvailableCapability(
        RasterProviderOperationCapabilityRow row) => new()
    {
        ProviderId = row.ProviderId,
        Engine = row.Engine,
        Variant = new RasterSemanticVariant
        {
            ProcessId = row.ProcessId,
            SemanticVersion = row.SemanticVersion,
            ImplementationVersion = row.ImplementationVersion,
        },
        PolicyVersion = row.PolicyVersion,
        Availability = RasterProviderAvailability.Available,
    };

    private static RasterProviderExecutableSemanticVariant Registration(
        RasterProviderOperationCapabilityRow row,
        IRasterProviderExecutor executor) => new()
    {
        Executor = executor,
        Capability = AvailableCapability(row),
        SemanticVariantId = row.SemanticVariantId,
    };

    private static RasterProviderSemanticProof Proof(RasterProviderOperationCapabilityRow row) => new()
    {
        ProviderId = row.ProviderId,
        Engine = row.Engine,
        ProcessId = row.ProcessId,
        SemanticVersion = row.SemanticVersion,
        SemanticVariantId = row.SemanticVariantId,
        ImplementationVersion = row.ImplementationVersion,
        PolicyVersion = row.PolicyVersion,
        FixtureId = row.RequiredFixtureIds.Single(),
        RuntimeVersion = "3.4.0",
        Passed = true,
    };

    private sealed class FakeExecutor(params RasterProviderCapability[] capabilities)
        : IRasterProviderExecutor
    {
        public IReadOnlyList<RasterProviderCapability> Capabilities { get; } = capabilities;

        public Task<RasterProviderExecutionResult> ExecuteAsync(
            RasterProviderExecutionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
