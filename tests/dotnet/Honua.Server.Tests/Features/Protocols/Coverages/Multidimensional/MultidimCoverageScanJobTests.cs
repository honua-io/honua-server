// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Server.Features.Protocols.Coverages.Multidimensional;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Unit coverage for the ADR-0039 Path B submit-side helper: spec projection
/// onto the GDAL native worker job, scan-job identification, and mapping the
/// worker's <c>gdalmdiminfo</c> artifact back to coverage metadata.
/// </summary>
public sealed class MultidimCoverageScanJobTests
{
    private const string GdalMdimInfoJson =
        """{"type":"group","driver":"netCDF","name":"/","arrays":{"sst":{"datatype":"Float32","dimensions":["/time"],"dimension_size":[3],"block_size":[1],"unit":"degC"}}}""";

    private const string GdalInfoJson =
        """{"geoTransform":[-156.55,0.1,0.0,20.85,0.0,-0.1],"cornerCoordinates":{"upperLeft":[-156.55,20.85],"lowerRight":[-156.05,20.45]},"metadata":{"":{"NETCDF_DIM_EXTRA":"{time}","time#units":"hours since 2026-01-01 00:00:00","NETCDF_DIM_time_VALUES":"{0,6,12}"}}}""";

    private static MultidimensionalCoverageRegistration Registration(long id = 7) => new()
    {
        Id = id,
        LayerId = 1,
        Name = "sst",
        Format = MultidimensionalCoverageFormat.NetCdf4,
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "honua-cubes",
        ObjectKey = "maui/sst.nc",
        Variables = Array.Empty<string>(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [UnitTest]
    public void BuildSpec_ProjectsProcessIdStepInputsAndNativeProfile()
    {
        var spec = MultidimCoverageScanJob.BuildSpec(Registration(id: 42));

        spec.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        spec.RuntimeProfile.Should().Be(RuntimeProfiles.Native);

        spec.Parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions]
            .Should().Be(MultidimCoverageScanJob.ProcessId);
        spec.Parameters["honua.geoprocessing.step.0.provider"].Should().Be("AwsS3");
        spec.Parameters["honua.geoprocessing.step.0.bucket"].Should().Be("honua-cubes");
        spec.Parameters["honua.geoprocessing.step.0.objectKey"].Should().Be("maui/sst.nc");
        spec.Parameters[MultidimCoverageScanJob.RegistrationIdParam].Should().Be("42");
    }

    [UnitTest]
    public void IsScanJob_TrueForScanSpec_FalseOtherwise()
    {
        var scan = JobWith(MultidimCoverageScanJob.BuildSpec(Registration()));
        MultidimCoverageScanJob.IsScanJob(scan).Should().BeTrue();

        var other = JobWith(new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "other",
            Parameters = new Dictionary<string, string>
            {
                [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "gdal.ogr2ogr",
            },
        });
        MultidimCoverageScanJob.IsScanJob(other).Should().BeFalse();
    }

    [UnitTest]
    public void TryGetRegistrationId_RoundTrips()
    {
        var job = JobWith(MultidimCoverageScanJob.BuildSpec(Registration(id: 99)));

        MultidimCoverageScanJob.TryGetRegistrationId(job, out var id).Should().BeTrue();
        id.Should().Be(99);
    }

    [UnitTest]
    public void TryMapArtifact_DecodesEnvelopeMapsAndEnriches()
    {
        var envelope =
            $$"""{"mdiminfo":{{GdalMdimInfoJson}},"info":{{GdalInfoJson}}}""";
        var artifact = "data:application/json;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));

        var metadata = MultidimCoverageScanJob.TryMapArtifact(
            artifact, MultidimensionalCoverageFormat.NetCdf4, Array.Empty<string>());

        metadata.Should().NotBeNull();
        metadata!.Variables.Should().ContainSingle().Which.Name.Should().Be("sst");
        // Enrichment from the gdalinfo block fills extent + resolution.
        metadata.Extent.Should().NotBeNull();
        metadata.Resolution.X.Should().BeApproximately(0.1, 1e-9);
    }

    [UnitTest]
    public void TryMapArtifact_MdimOnlyEnvelope_StillMaps()
    {
        var envelope = $$"""{"mdiminfo":{{GdalMdimInfoJson}}}""";
        var artifact = "data:application/json;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));

        var metadata = MultidimCoverageScanJob.TryMapArtifact(
            artifact, MultidimensionalCoverageFormat.NetCdf4, Array.Empty<string>());

        metadata.Should().NotBeNull();
        metadata!.Variables.Should().ContainSingle().Which.Name.Should().Be("sst");
    }

    [UnitTest]
    public void TryMapArtifact_RejectsNonDataUriOrWrongType()
    {
        MultidimCoverageScanJob.TryMapArtifact(
            null, MultidimensionalCoverageFormat.NetCdf4, Array.Empty<string>()).Should().BeNull();
        MultidimCoverageScanJob.TryMapArtifact(
            "not-a-data-uri", MultidimensionalCoverageFormat.NetCdf4, Array.Empty<string>()).Should().BeNull();
        MultidimCoverageScanJob.TryMapArtifact(
            "data:text/plain;base64,QQ==", MultidimensionalCoverageFormat.NetCdf4, Array.Empty<string>()).Should().BeNull();
    }

    private static ExecutionJobRecord JobWith(ExecutionJobSpec spec)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "covscan-test",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = spec,
        };
    }
}
