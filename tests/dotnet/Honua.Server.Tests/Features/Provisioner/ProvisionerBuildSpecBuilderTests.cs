// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Provisioner.BuildJobs;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Provisioner;

/// <summary>
/// Unit coverage for the geocoder/router build execution-job spec encode/decode contract.
/// Every behavior-changing build parameter (feedstock source/product, area, table, schema,
/// artifact name/key, artifact kind) must round-trip from the build request through
/// <see cref="ExecutionJobSpec.Parameters"/> and back so the GP-on-Batch worker builds
/// exactly the locator/graph the caller requested for the area.
/// </summary>
public sealed class ProvisionerBuildSpecBuilderTests
{
    private static ProvisionerArea MauiCounty()
    {
        ProvisionerArea.TryParse("geoid:15009", out var area, out _).Should().BeTrue();
        return area;
    }

    private static ProvisionerBuildBatchOptions AwsBatchOptions() => new()
    {
        Enabled = true,
        Backend = "honua-aws-batch",
        TargetKind = BatchComputeTargetKind.AwsBatch,
        Artifact = "ecr/honua-build-worker:1",
        GeocoderArtifact = "ecr/honua-nominatim-build:1",
        RouterArtifact = "ecr/honua-osm2pgrouting:1",
        RuntimeProfile = "gdal",
        Parameters =
        {
            ["batch.job_definition_arn"] = "arn:jd:1",
            ["batch.job_queue_arn"] = "arn:jq:1",
            ["provisioner.artifact_bucket"] = "s3://honua-artifacts"
        }
    };

    // ---- Geocoder build -----------------------------------------------------

    [UnitTest]
    public void GeocoderBuild_EncodesAllParameters_OntoSpec()
    {
        var request = new GeocoderBuildRequest
        {
            SourceId = "census-tiger",
            ProductId = "addresses",
            Area = MauiCounty(),
            FeedstockTable = "od_census_tiger_addresses",
            SchemaName = "public",
            ArtifactName = "maui",
            ArtifactKey = "locators/maui/maui.osm.pbf"
        };

        var spec = GeocoderBuildExecutionSpecBuilder.Build(request, AwsBatchOptions());

        spec.Kind.Should().Be(ExecutionJobKind.GeocoderBuild);
        spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        spec.Backend.Should().Be("honua-aws-batch");
        // Per-kind artifact override wins over the default Artifact.
        spec.Artifact.Should().Be("ecr/honua-nominatim-build:1");
        spec.RuntimeProfile.Should().Be("gdal");
        spec.WorkloadName.Should().Be("geocoder-build:maui:geoid:15009");

        spec.Parameters[ProvisionerBuildJobParameterKeys.SourceId].Should().Be("census-tiger");
        spec.Parameters[ProvisionerBuildJobParameterKeys.ProductId].Should().Be("addresses");
        spec.Parameters[ProvisionerBuildJobParameterKeys.Area].Should().Be("geoid:15009");
        spec.Parameters[ProvisionerBuildJobParameterKeys.FeedstockTable].Should().Be("od_census_tiger_addresses");
        spec.Parameters[ProvisionerBuildJobParameterKeys.ArtifactName].Should().Be("maui");
        spec.Parameters[ProvisionerBuildJobParameterKeys.ArtifactKey].Should().Be("locators/maui/maui.osm.pbf");
        spec.Parameters[ProvisionerBuildJobParameterKeys.LocatorKind].Should().Be(GeocoderArtifactKinds.NominatimPbf);
        // Backend coordinates merged in.
        spec.Parameters["batch.job_definition_arn"].Should().Be("arn:jd:1");
        spec.Parameters["provisioner.artifact_bucket"].Should().Be("s3://honua-artifacts");
    }

    [UnitTest]
    public void GeocoderBuild_RoundTrips_ThroughParameters()
    {
        var request = new GeocoderBuildRequest
        {
            SourceId = "osm-geofabrik",
            ProductId = "addresses",
            Area = MauiCounty(),
            FeedstockTable = "od_osm_geofabrik_addresses",
            SchemaName = "demo",
            ArtifactName = "maui",
            ArtifactKey = "locators/maui/maui.osm.pbf"
        };

        var spec = GeocoderBuildExecutionSpecBuilder.Build(request, AwsBatchOptions());

        GeocoderBuildExecutionSpecBuilder.TryParse(spec.Parameters, out var parsed, out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
        parsed.SourceId.Should().Be("osm-geofabrik");
        parsed.ProductId.Should().Be("addresses");
        parsed.Area.Raw.Should().Be("geoid:15009");
        parsed.Area.CountyGeoid.Should().Be("15009");
        parsed.FeedstockTable.Should().Be("od_osm_geofabrik_addresses");
        parsed.SchemaName.Should().Be("demo");
        parsed.ArtifactName.Should().Be("maui");
        parsed.ArtifactKey.Should().Be("locators/maui/maui.osm.pbf");
        parsed.LocatorKind.Should().Be(GeocoderArtifactKinds.NominatimPbf);
    }

    [UnitTest]
    public void GeocoderBuild_TryParse_MissingFeedstock_FailsCleanly()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProvisionerBuildJobParameterKeys.SourceId] = "census-tiger",
            [ProvisionerBuildJobParameterKeys.ProductId] = "addresses",
            [ProvisionerBuildJobParameterKeys.Area] = "geoid:15009",
            [ProvisionerBuildJobParameterKeys.ArtifactName] = "maui",
            [ProvisionerBuildJobParameterKeys.ArtifactKey] = "locators/maui/maui.osm.pbf"
            // FeedstockTable intentionally absent.
        };

        GeocoderBuildExecutionSpecBuilder.TryParse(parameters, out _, out var error).Should().BeFalse();
        error.Should().Contain(ProvisionerBuildJobParameterKeys.FeedstockTable);
    }

    [UnitTest]
    public void GeocoderBuild_TryParse_MalformedArea_FailsCleanly()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProvisionerBuildJobParameterKeys.SourceId] = "census-tiger",
            [ProvisionerBuildJobParameterKeys.ProductId] = "addresses",
            [ProvisionerBuildJobParameterKeys.Area] = "geoid:NOPE",
            [ProvisionerBuildJobParameterKeys.FeedstockTable] = "t",
            [ProvisionerBuildJobParameterKeys.ArtifactName] = "maui",
            [ProvisionerBuildJobParameterKeys.ArtifactKey] = "k"
        };

        GeocoderBuildExecutionSpecBuilder.TryParse(parameters, out _, out var error).Should().BeFalse();
        error.Should().Contain("numeric");
    }

    // ---- Router build -------------------------------------------------------

    [UnitTest]
    public void RouterBuild_EncodesAllParameters_OntoSpec()
    {
        var request = new RouterBuildRequest
        {
            SourceId = "census-tiger",
            ProductId = "routing-roads",
            Area = MauiCounty(),
            FeedstockTable = "od_census_tiger_routing_roads",
            SchemaName = "public",
            ArtifactName = "maui",
            ArtifactKey = "routing/maui/ways.dump"
        };

        var spec = RouterBuildExecutionSpecBuilder.Build(request, AwsBatchOptions());

        spec.Kind.Should().Be(ExecutionJobKind.RouterBuild);
        spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        spec.Artifact.Should().Be("ecr/honua-osm2pgrouting:1");
        spec.WorkloadName.Should().Be("router-build:maui:geoid:15009");
        spec.Parameters[ProvisionerBuildJobParameterKeys.GraphKind].Should().Be(RouterArtifactKinds.PgRoutingTopology);
        spec.Parameters[ProvisionerBuildJobParameterKeys.FeedstockTable].Should().Be("od_census_tiger_routing_roads");
        spec.Parameters["batch.job_queue_arn"].Should().Be("arn:jq:1");
    }

    [UnitTest]
    public void RouterBuild_RoundTrips_ThroughParameters()
    {
        ProvisionerArea.TryParse("bbox:-156.70,20.57,-155.98,21.03", out var bbox, out _).Should().BeTrue();
        var request = new RouterBuildRequest
        {
            SourceId = "osm-geofabrik",
            ProductId = "routing-roads",
            Area = bbox,
            FeedstockTable = "od_osm_geofabrik_routing_roads",
            SchemaName = "public",
            ArtifactName = "maui",
            ArtifactKey = "routing/maui/ways.dump"
        };

        var spec = RouterBuildExecutionSpecBuilder.Build(request, AwsBatchOptions());

        RouterBuildExecutionSpecBuilder.TryParse(spec.Parameters, out var parsed, out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
        parsed.SourceId.Should().Be("osm-geofabrik");
        parsed.Area.Kind.Should().Be(ProvisionerAreaKind.Bbox);
        parsed.Area.Bbox.Should().Equal(-156.70, 20.57, -155.98, 21.03);
        parsed.FeedstockTable.Should().Be("od_osm_geofabrik_routing_roads");
        parsed.GraphKind.Should().Be(RouterArtifactKinds.PgRoutingTopology);
    }

    [UnitTest]
    public void RouterBuild_LocalBackend_DefaultsArtifactToShared()
    {
        var options = new ProvisionerBuildBatchOptions
        {
            Enabled = true,
            Backend = "local",
            Artifact = "shared-image"
            // No RouterArtifact override.
        };
        var request = new RouterBuildRequest
        {
            SourceId = "census-tiger",
            ProductId = "routing-roads",
            Area = MauiCounty(),
            FeedstockTable = "t",
            ArtifactName = "maui",
            ArtifactKey = "k"
        };

        var spec = RouterBuildExecutionSpecBuilder.Build(request, options);

        spec.Artifact.Should().Be("shared-image");
        spec.Backend.Should().Be("local");
    }
}
