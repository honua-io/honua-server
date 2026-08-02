// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>Tests typed raster references projected onto durable GP specifications.</summary>
public sealed class GeoprocessingRasterSourceSpecTests
{
    [UnitTest]
    public void BuildNoWorkloadSpec_ObjectReference_UsesV2ReferenceContractWithoutRasterBytes()
    {
        var descriptor = Cog();
        var plan = Plan(descriptor);

        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(plan, [], requiredRuntimeProfile: "native");

        Assert.Equal(RasterSourceContract.JobContractVersion, spec.ContractVersion);
        var parameter = Assert.Single(
            spec.Parameters,
            pair => pair.Key.StartsWith(
                ExecutionJobParameterKeys.GeoprocessingStepRasterSourcePrefix,
                StringComparison.Ordinal));
        Assert.DoesNotContain("payload", parameter.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base64", parameter.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(descriptor, RasterSourceJson.Deserialize(parameter.Value));
    }

    [UnitTest]
    public void BuildNoWorkloadSpec_PlanWithoutTypedRasterSource_RemainsV1Compatible()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "plan-managed",
            IntentId = "intent-managed",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-0",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string> { ["distance"] = "10" },
                },
            ],
        };

        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(plan, [], requiredRuntimeProfile: null);

        Assert.Equal(1, spec.ContractVersion);
        Assert.DoesNotContain(
            spec.Parameters,
            pair => pair.Key.StartsWith(
                ExecutionJobParameterKeys.GeoprocessingStepRasterSourcePrefix,
                StringComparison.Ordinal));
    }

    [UnitTest]
    public void ProjectPlanParameters_InlineAboveDefaultCeiling_ThrowsBeforeSpecProjection()
    {
        var descriptor = new InlineRasterSourceDescriptor
        {
            Version = "inline-v1",
            Payload = new byte[RasterSourceValidationOptions.Default.MaxInlineBytes + 1],
            Content = Content(RasterSourceValidationOptions.Default.MaxInlineBytes + 1),
            SecurityContext = Security(),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            GeoprocessingSpecBuilder.ProjectPlanParameters(Plan(descriptor), []));

        Assert.Contains(RasterSourceValidationCodes.InlinePayloadTooLarge, exception.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void CreateRequestFingerprint_ChangedRasterVersion_ChangesFingerprint()
    {
        var first = Plan(Cog());
        var second = Plan(Cog() with { Version = "object-version-2" });

        var firstFingerprint = GeoprocessingJobService.CreateRequestFingerprint(first);
        var secondFingerprint = GeoprocessingJobService.CreateRequestFingerprint(second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    private static AnalysisPlan Plan(RasterSourceDescriptor descriptor) => new()
    {
        PlanId = "plan-raster",
        IntentId = "intent-raster",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-0",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "raster.clip",
                RasterSources = new Dictionary<string, RasterSourceDescriptor>
                {
                    ["source"] = descriptor,
                },
            },
        ],
    };

    private static ObjectStoreCogRasterSourceDescriptor Cog() => new()
    {
        Version = "object-version-1",
        Provider = CloudStorageProvider.AwsS3,
        StoreReference = "imagery-prod",
        ObjectKey = "tenant-a/imagery/source.tif",
        Content = Content(4096) with { MediaType = "image/tiff", ETag = "etag-1" },
        SecurityContext = Security(),
    };

    private static RasterContentIdentity Content(long size) => new()
    {
        SizeBytes = size,
        MediaType = "application/octet-stream",
        Checksum = new RasterChecksum("sha256", new string('a', 64)),
    };

    private static RasterSecurityContextReference Security() => new()
    {
        TenantId = "tenant-a",
        AuthorizationSnapshotReference = "auth-snapshot-123",
    };
}
