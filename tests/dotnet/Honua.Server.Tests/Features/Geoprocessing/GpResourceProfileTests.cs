// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit coverage for the per-job <see cref="GpResourceProfile"/> (honua-server#2165): the typed
/// vCPU/memory/GPU/timeout/retry/ephemeral/arch profile the GP job spec carries and the mapping
/// onto the <c>batch.*</c> contract keys that <c>AwsBatchComputeBackend</c> consumes for SubmitJob
/// overrides and ephemeral job-definition tier selection. Fully offline — no AWS, no Redis.
/// </summary>
public sealed class GpResourceProfileTests
{
    private static ProcessDefinition Process(
        string processId,
        string category,
        string runtimeProfile = RuntimeProfilesManaged,
        params ArtifactKind[] outputs)
        => new()
        {
            ProcessId = processId,
            Title = processId,
            Description = processId,
            Category = category,
            Parameters = [],
            OutputArtifactKinds = outputs.Length > 0 ? outputs : [ArtifactKind.File],
            RuntimeProfile = runtimeProfile,
        };

    private const string RuntimeProfilesManaged = "managed";
    private const string RuntimeProfilesNative = "native";

    [UnitTest]
    public void ForProcess_ManagedFeatureProcess_UsesSmallestTier()
    {
        var profile = GpResourceProfile.ForProcess(
            Process("geometry.buffer", "geometry", outputs: ArtifactKind.FeatureLayer));

        profile.Vcpus.Should().Be(1);
        profile.MemoryMib.Should().Be(2048);
        profile.EphemeralGib.Should().Be(20);
        profile.GpuCount.Should().BeNull();
    }

    [UnitTest]
    public void ForProcess_NativeProcess_UsesMidTier()
    {
        var profile = GpResourceProfile.ForProcess(
            Process("gdal.gdalwarp", "conversion", RuntimeProfilesNative, ArtifactKind.File));

        profile.Vcpus.Should().Be(2);
        profile.MemoryMib.Should().Be(4096);
        profile.EphemeralGib.Should().Be(50);
    }

    [UnitTest]
    public void ForProcess_RasterClassProcess_UsesLargestTierWithTimeout()
    {
        var profile = GpResourceProfile.ForProcess(
            Process("surface.hillshade", "surface", RuntimeProfilesNative, ArtifactKind.Raster));

        profile.Vcpus.Should().Be(4);
        profile.MemoryMib.Should().Be(8192);
        profile.EphemeralGib.Should().Be(100);
        profile.TimeoutSeconds.Should().Be(3600);
    }

    [UnitTest]
    public void FromRequestParameters_ReadsExplicitValuesAndIgnoresNonPositive()
    {
        var profile = GpResourceProfile.FromRequestParameters(new Dictionary<string, string>
        {
            ["gp.resource.vcpus"] = "8",
            ["gp.resource.memory_mib"] = "16384",
            ["gp.resource.gpu_count"] = "1",
            ["gp.resource.timeout_seconds"] = "7200",
            ["gp.resource.retry_attempts"] = "2",
            ["gp.resource.ephemeral_gib"] = "150",
            ["gp.resource.arch"] = "arm64",
            ["unrelated"] = "x",
        });

        profile.Vcpus.Should().Be(8);
        profile.MemoryMib.Should().Be(16384);
        profile.GpuCount.Should().Be(1);
        profile.TimeoutSeconds.Should().Be(7200);
        profile.RetryAttempts.Should().Be(2);
        profile.EphemeralGib.Should().Be(150);
        profile.Arch.Should().Be("arm64");
    }

    [UnitTest]
    public void FromRequestParameters_IgnoresBlankAndUnparseable()
    {
        var profile = GpResourceProfile.FromRequestParameters(new Dictionary<string, string>
        {
            ["gp.resource.vcpus"] = "",
            ["gp.resource.memory_mib"] = "lots",
            ["gp.resource.ephemeral_gib"] = "0",
        });

        profile.IsEmpty.Should().BeTrue();
    }

    [UnitTest]
    public void FromRequestParameters_AllowsZeroGpuCount()
    {
        var profile = GpResourceProfile.FromRequestParameters(new Dictionary<string, string>
        {
            ["gp.resource.gpu_count"] = "0",
        });

        profile.GpuCount.Should().Be(0);
    }

    [UnitTest]
    public void MergeMax_TakesHeavierValueOfEachDimension()
    {
        var light = GpResourceProfile.ForProcess(
            Process("geometry.buffer", "geometry", outputs: ArtifactKind.FeatureLayer));
        var heavy = GpResourceProfile.ForProcess(
            Process("surface.hillshade", "surface", RuntimeProfilesNative, ArtifactKind.Raster));

        var merged = light.MergeMax(heavy);

        merged.Vcpus.Should().Be(4);
        merged.MemoryMib.Should().Be(8192);
        merged.EphemeralGib.Should().Be(100);
        merged.TimeoutSeconds.Should().Be(3600);
    }

    [UnitTest]
    public void OverrideWith_ExplicitValuesWinFieldByField()
    {
        var derived = GpResourceProfile.ForProcess(
            Process("geometry.buffer", "geometry", outputs: ArtifactKind.FeatureLayer));
        var overrides = new GpResourceProfile { Vcpus = 16, EphemeralGib = 200 };

        var effective = derived.OverrideWith(overrides);

        effective.Vcpus.Should().Be(16);
        effective.EphemeralGib.Should().Be(200);
        // Unset override fields keep the derived value.
        effective.MemoryMib.Should().Be(2048);
    }

    [UnitTest]
    public void ProjectOnto_WritesBatchKeysForSetFields()
    {
        var profile = new GpResourceProfile
        {
            Vcpus = 4,
            MemoryMib = 8192,
            GpuCount = 1,
            TimeoutSeconds = 3600,
            RetryAttempts = 2,
            EphemeralGib = 100,
            Arch = "arm64",
        };
        var specParams = new Dictionary<string, string>(StringComparer.Ordinal);

        profile.ProjectOnto(specParams);

        specParams["batch.vcpus"].Should().Be("4");
        specParams["batch.memory_mib"].Should().Be("8192");
        specParams["batch.gpu_count"].Should().Be("1");
        specParams["batch.timeout_seconds"].Should().Be("3600");
        specParams["batch.retry_attempts"].Should().Be("2");
        specParams["batch.ephemeral_gib"].Should().Be("100");
        specParams["batch.arch"].Should().Be("arm64");
    }

    [UnitTest]
    public void ProjectOnto_DoesNotOverwriteExistingKeys()
    {
        var profile = new GpResourceProfile { Vcpus = 4, EphemeralGib = 100 };
        var specParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["batch.vcpus"] = "16",
        };

        profile.ProjectOnto(specParams);

        // An explicit raw batch.* value already on the bag wins (set-if-absent semantics).
        specParams["batch.vcpus"].Should().Be("16");
        specParams["batch.ephemeral_gib"].Should().Be("100");
    }

    [UnitTest]
    public void ProjectOnto_EmptyProfileWritesNothing()
    {
        var specParams = new Dictionary<string, string>(StringComparer.Ordinal);

        GpResourceProfile.Empty.ProjectOnto(specParams);

        specParams.Should().BeEmpty();
    }

    [UnitTest]
    public void ProjectOnto_KubernetesTargetWritesSupportedDynamicResources()
    {
        var profile = new GpResourceProfile
        {
            Vcpus = 4,
            MemoryMib = 8192,
            TimeoutSeconds = 900,
            EphemeralGib = 100,
        };
        var specParams = new Dictionary<string, string>(StringComparer.Ordinal);

        profile.ProjectOnto(specParams, BatchComputeTargetKind.KubernetesJob);

        specParams["k8s.cpu_request"].Should().Be("4");
        specParams["k8s.cpu_limit"].Should().Be("4");
        specParams["k8s.memory_request"].Should().Be("8192Mi");
        specParams["k8s.memory_limit"].Should().Be("8192Mi");
        specParams["k8s.active_deadline_seconds"].Should().Be("900");
        specParams.Should().NotContainKey("batch.ephemeral_gib");
    }

    [UnitTest]
    public void ProjectOnto_AzureBatchTargetWritesSupportedExecutionPolicy()
    {
        var profile = new GpResourceProfile
        {
            TimeoutSeconds = 61,
            RetryAttempts = 3,
        };
        var specParams = new Dictionary<string, string>(StringComparer.Ordinal);

        profile.ProjectOnto(specParams, BatchComputeTargetKind.AzureBatch);

        specParams["azure.batch.task_timeout_minutes"].Should().Be("2");
        specParams["azure.batch.max_task_retry_count"].Should().Be("2");
    }

    [UnitTest]
    public void ProjectOnto_AzureBatchOneTotalAttemptWritesZeroRetries()
    {
        var specParams = new Dictionary<string, string>(StringComparer.Ordinal);

        new GpResourceProfile { RetryAttempts = 1 }
            .ProjectOnto(specParams, BatchComputeTargetKind.AzureBatch);

        specParams["azure.batch.max_task_retry_count"].Should().Be("0");
    }
}
