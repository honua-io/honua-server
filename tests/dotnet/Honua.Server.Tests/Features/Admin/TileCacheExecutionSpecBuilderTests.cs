// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit coverage for the tile-cache execution-job spec encode/decode contract
/// (issue #1697): every behavior-changing tile parameter must round-trip from a
/// <see cref="TileOperationStartRequest"/> through <see cref="ExecutionJobSpec.Parameters"/>
/// and back so the worker executes exactly what the caller requested.
/// </summary>
public sealed class TileCacheExecutionSpecBuilderTests
{
    [UnitTest]
    public void Build_EncodesAllTileParameters_OntoSpec()
    {
        var request = new TileOperationStartRequest
        {
            Operation = "archive",
            ServiceId = "svc-1",
            LayerId = 42,
            MinZoom = 3,
            MaxZoom = 8,
            TileMatrixSetId = "WebMercatorQuad",
            Bbox = [-10d, -20d, 30d, 40d],
            MaxTiles = 25_000
        };

        var options = new TileCacheBatchOptions
        {
            Enabled = true,
            Backend = "honua-aws-batch",
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Artifact = "ecr/honua-tile-worker:1",
            RuntimeProfile = "native",
            Parameters =
            {
                ["batch.job_definition_arn"] = "arn:jd:1",
                ["batch.job_queue_arn"] = "arn:jq:1"
            }
        };

        var spec = TileCacheExecutionSpecBuilder.Build(request, "tenant-schema", options);

        spec.Kind.Should().Be(ExecutionJobKind.TileCache);
        spec.Backend.Should().Be("honua-aws-batch");
        spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        spec.Artifact.Should().Be("ecr/honua-tile-worker:1");
        spec.RuntimeProfile.Should().Be("native");
        spec.WorkloadName.Should().Contain("archive").And.Contain("42");

        spec.Parameters[TileCacheJobParameterKeys.Operation].Should().Be("archive");
        spec.Parameters[TileCacheJobParameterKeys.ServiceId].Should().Be("svc-1");
        spec.Parameters[TileCacheJobParameterKeys.LayerId].Should().Be("42");
        spec.Parameters[TileCacheJobParameterKeys.MinZoom].Should().Be("3");
        spec.Parameters[TileCacheJobParameterKeys.MaxZoom].Should().Be("8");
        spec.Parameters[TileCacheJobParameterKeys.TileMatrixSetId].Should().Be("WebMercatorQuad");
        spec.Parameters[TileCacheJobParameterKeys.MaxTiles].Should().Be("25000");
        spec.Parameters[TileCacheJobParameterKeys.SchemaName].Should().Be("tenant-schema");
        spec.Parameters[TileCacheJobParameterKeys.Bbox].Should().Be("-10,-20,30,40");
        spec.Parameters["batch.job_definition_arn"].Should().Be("arn:jd:1");
        spec.Parameters["batch.job_queue_arn"].Should().Be("arn:jq:1");
    }

    [UnitTest]
    public void BuildThenTryParse_RoundTrips_AllFields()
    {
        var request = new TileOperationStartRequest
        {
            Operation = "publish",
            ServiceId = "svc-9",
            LayerId = 7,
            MinZoom = 0,
            MaxZoom = 12,
            TileMatrixSetId = "WebMercatorQuad",
            Bbox = [1.5, 2.5, 3.5, 4.5],
            MaxTiles = 100_000
        };

        var options = new TileCacheBatchOptions { Enabled = true, Backend = "local" };
        var spec = TileCacheExecutionSpecBuilder.Build(request, "schema-x", options);

        var parsed = TileCacheExecutionSpecBuilder.TryParse(
            spec.Parameters, out var decoded, out var schemaName, out var error);

        parsed.Should().BeTrue();
        error.Should().BeEmpty();
        schemaName.Should().Be("schema-x");
        decoded.Operation.Should().Be("publish");
        decoded.ServiceId.Should().Be("svc-9");
        decoded.LayerId.Should().Be(7);
        decoded.MinZoom.Should().Be(0);
        decoded.MaxZoom.Should().Be(12);
        decoded.TileMatrixSetId.Should().Be("WebMercatorQuad");
        decoded.MaxTiles.Should().Be(100_000);
        decoded.Bbox.Should().NotBeNull();
        decoded.Bbox!.Should().Equal(1.5, 2.5, 3.5, 4.5);
    }

    [UnitTest]
    public void BuildThenTryParse_RoundTrips_GenerationId()
    {
        var request = new TileOperationStartRequest
        {
            Operation = "seed",
            LayerId = 3,
            TileMatrixSetId = "WebMercatorQuad",
            GenerationId = "gen-abc-123"
        };

        var spec = TileCacheExecutionSpecBuilder.Build(request, schemaName: null, new TileCacheBatchOptions());
        spec.Parameters[TileCacheJobParameterKeys.GenerationId].Should().Be("gen-abc-123");

        var parsed = TileCacheExecutionSpecBuilder.TryParse(
            spec.Parameters, out var decoded, out _, out var error);

        parsed.Should().BeTrue();
        error.Should().BeEmpty();
        decoded.GenerationId.Should().Be("gen-abc-123");
    }

    [UnitTest]
    public void TryParse_SpecWithoutGenerationId_ParsesWithNullGeneration()
    {
        // Back-compat: a spec produced before the generation-id field existed must still parse,
        // leaving GenerationId null so the worker anchors it to the operation id.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TileCacheJobParameterKeys.Operation] = "seed",
            [TileCacheJobParameterKeys.LayerId] = "5",
            [TileCacheJobParameterKeys.TileMatrixSetId] = "WebMercatorQuad"
        };

        var parsed = TileCacheExecutionSpecBuilder.TryParse(
            parameters, out var decoded, out _, out var error);

        parsed.Should().BeTrue();
        error.Should().BeEmpty();
        decoded.GenerationId.Should().BeNull();
        decoded.LayerId.Should().Be(5);
    }

    [UnitTest]
    public void Build_WithoutGenerationId_OmitsParameter()
    {
        var request = new TileOperationStartRequest
        {
            Operation = "warm",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad"
        };

        var spec = TileCacheExecutionSpecBuilder.Build(request, schemaName: null, new TileCacheBatchOptions());

        spec.Parameters.ContainsKey(TileCacheJobParameterKeys.GenerationId).Should().BeFalse();
    }

    [UnitTest]
    public void TryParse_MissingOperation_Fails()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TileCacheJobParameterKeys.LayerId] = "1"
        };

        var parsed = TileCacheExecutionSpecBuilder.TryParse(
            parameters, out _, out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("operation");
    }

    [UnitTest]
    public void TryParse_MalformedLayerId_Fails()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TileCacheJobParameterKeys.Operation] = "seed",
            [TileCacheJobParameterKeys.LayerId] = "not-a-number"
        };

        var parsed = TileCacheExecutionSpecBuilder.TryParse(
            parameters, out _, out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("layer_id");
    }
}
