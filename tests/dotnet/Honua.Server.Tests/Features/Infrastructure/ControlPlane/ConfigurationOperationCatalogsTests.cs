// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ConfigurationOperationCatalogsTests
{
    [Fact]
    public async Task DeployTargetRegistry_MergesParameterEntriesWithDictionaryParameters()
    {
        var registry = new ConfigurationDeployTargetRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                DeployTargets =
                [
                    new DeployTargetOptions
                    {
                        TargetId = "lambda-prod",
                        Backend = "honua-gitops-aws-lambda",
                        Environment = "prod",
                        TargetName = "honua-prod-lambda",
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["existing"] = "value",
                            ["aws.region"] = "us-west-2"
                        },
                        ParameterEntries =
                        [
                            new ConfigurationParameterEntryOptions
                            {
                                Key = "aws.lambda.function_name",
                                Value = "honua-prod-lambda"
                            },
                            new ConfigurationParameterEntryOptions
                            {
                                Key = "aws.region",
                                Value = "us-east-1"
                            }
                        ]
                    }
                ]
            }));

        var target = await registry.GetAsync("lambda-prod");

        target.Should().NotBeNull();
        target!.Parameters.Should().Contain(new KeyValuePair<string, string>("existing", "value"));
        target.Parameters.Should().Contain(new KeyValuePair<string, string>("aws.lambda.function_name", "honua-prod-lambda"));
        target.Parameters.Should().Contain(new KeyValuePair<string, string>("aws.region", "us-east-1"));
    }

    [Fact]
    public async Task ExecutionJobRegistry_MergesParameterEntriesWithDictionaryParameters()
    {
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "python-geoprocessing",
                        Backend = "aws-batch",
                        WorkloadName = "Python GP",
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["runtime"] = "python"
                        },
                        ParameterEntries =
                        [
                            new ConfigurationParameterEntryOptions
                            {
                                Key = "image.uri",
                                Value = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-gp:py311"
                            }
                        ]
                    }
                ]
            }));

        var workload = await registry.GetAsync("python-geoprocessing");

        workload.Should().NotBeNull();
        workload!.Parameters.Should().Contain(new KeyValuePair<string, string>("runtime", "python"));
        workload.Parameters.Should().Contain(new KeyValuePair<string, string>("image.uri", "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-gp:py311"));
    }

    [Fact]
    public async Task ExecutionJobRegistry_DropsAwsBatchWorkload_WhenSubstrateArnsAreEmpty()
    {
        // The committed placeholder AWS Batch GP workload ships with empty ARN
        // parameters so it is discoverable; the activation gate must drop it until an
        // operator override supplies the substrate ARNs, leaving local to win routing.
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "geoprocessing-local",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.KubernetesJob,
                        Backend = "local",
                        WorkloadName = "Geoprocessing (local baseline)"
                    },
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "geoprocessing-aws-batch",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.AwsBatch,
                        Backend = "honua-aws-batch",
                        WorkloadName = "Geoprocessing (AWS Batch)",
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["batch.job_queue_arn"] = string.Empty,
                            ["batch.region"] = string.Empty,
                            ["batch.job_definition_arn.s"] = string.Empty,
                            ["batch.job_definition_arn.xl"] = string.Empty
                        }
                    }
                ]
            }));

        var definitions = await registry.ListAsync();

        definitions.Should().ContainSingle()
            .Which.WorkloadId.Should().Be("geoprocessing-local");
    }

    [Fact]
    public async Task ExecutionJobRegistry_KeepsAwsBatchWorkload_WhenQueueAndTierArnPresent()
    {
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "geoprocessing-aws-batch",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.AwsBatch,
                        Backend = "honua-aws-batch",
                        WorkloadName = "Geoprocessing (AWS Batch)",
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["batch.job_queue_arn"] = "arn:aws:batch:us-east-1:123:job-queue/honua-gp",
                            ["batch.job_definition_arn.s"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-s:1"
                        }
                    }
                ]
            }));

        var workload = await registry.GetAsync("geoprocessing-aws-batch");

        workload.Should().NotBeNull();
        workload!.Backend.Should().Be("honua-aws-batch");
    }

    [Fact]
    public async Task ExecutionJobRegistry_DropsAwsBatchWorkload_WhenQueuePresentButNoJobDefinition()
    {
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "geoprocessing-aws-batch",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.AwsBatch,
                        Backend = "honua-aws-batch",
                        WorkloadName = "Geoprocessing (AWS Batch)",
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["batch.job_queue_arn"] = "arn:aws:batch:us-east-1:123:job-queue/honua-gp"
                        }
                    }
                ]
            }));

        var definitions = await registry.ListAsync();

        definitions.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecutionJobRegistry_KeepsAwsBatchWorkload_WhenSingleNonTieredArnPresent()
    {
        // Back-compat: a non-tiered config with the single batch.job_definition_arn key
        // (the pre-tier-selection wiring path) must stay active.
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "geoprocessing-aws-batch",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.AwsBatch,
                        Backend = "honua-aws-batch",
                        WorkloadName = "Geoprocessing (AWS Batch)",
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["batch.job_queue_arn"] = "arn:aws:batch:us-east-1:123:job-queue/honua-gp",
                            ["batch.job_definition_arn"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp:1"
                        }
                    }
                ]
            }));

        var workload = await registry.GetAsync("geoprocessing-aws-batch");

        workload.Should().NotBeNull();
    }

    [Fact]
    public async Task DeployTargetRegistry_ProjectsPlatformReleaseServingArtifact_WhenTargetHasNoExplicitArtifact()
    {
        var registry = new ConfigurationDeployTargetRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                PlatformRelease = new PlatformReleaseOptions
                {
                    Version = "2026.07.0",
                    ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
                    Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "ghcr.io/honua/worker:2026.07.0" }]
                },
                DeployTargets =
                [
                    new DeployTargetOptions
                    {
                        TargetId = "serving-prod",
                        Backend = "honua-gitops-kubernetes",
                        Environment = "prod",
                        TargetName = "honua-prod"
                    }
                ]
            }));

        var target = await registry.GetAsync("serving-prod");

        target.Should().NotBeNull();
        target!.ArtifactReference.Should().Be("ghcr.io/honua/server:2026.07.0");
    }

    [Fact]
    public async Task DeployTargetRegistry_ExplicitArtifactWinsOverPlatformRelease()
    {
        var registry = new ConfigurationDeployTargetRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                PlatformRelease = new PlatformReleaseOptions
                {
                    Version = "2026.07.0",
                    ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
                    Workers = [new PlatformReleaseWorkerImageOptions { ArtifactReference = "ghcr.io/honua/worker:2026.07.0" }]
                },
                DeployTargets =
                [
                    new DeployTargetOptions
                    {
                        TargetId = "serving-prod",
                        Backend = "honua-gitops-kubernetes",
                        Environment = "prod",
                        TargetName = "honua-prod",
                        ArtifactReference = "ghcr.io/honua/server:pinned"
                    }
                ]
            }));

        var target = await registry.GetAsync("serving-prod");

        target!.ArtifactReference.Should().Be("ghcr.io/honua/server:pinned");
    }

    [Fact]
    public async Task ExecutionJobRegistry_ProjectsPlatformReleaseWorkerImage_MatchedByRuntimeProfile()
    {
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                PlatformRelease = new PlatformReleaseOptions
                {
                    Version = "2026.07.0",
                    ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
                    Workers =
                    [
                        new PlatformReleaseWorkerImageOptions { ArtifactReference = "ghcr.io/honua/worker:2026.07.0" },
                        new PlatformReleaseWorkerImageOptions { RuntimeProfile = "gdal", ArtifactReference = "ghcr.io/honua/worker-gdal:2026.07.0" }
                    ]
                },
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "gp-gdal",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.KubernetesJob,
                        Backend = "local",
                        WorkloadName = "GDAL GP",
                        RuntimeProfile = "gdal"
                    },
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "gp-default",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.KubernetesJob,
                        Backend = "local",
                        WorkloadName = "Default GP"
                    }
                ]
            }));

        var gdal = await registry.GetAsync("gp-gdal");
        var fallback = await registry.GetAsync("gp-default");

        gdal!.ArtifactReference.Should().Be("ghcr.io/honua/worker-gdal:2026.07.0");
        fallback!.ArtifactReference.Should().Be("ghcr.io/honua/worker:2026.07.0");
    }

    [Fact]
    public async Task ExecutionJobRegistry_ExplicitWorkloadArtifactWinsOverPlatformRelease()
    {
        var registry = new ConfigurationExecutionJobDefinitionRegistry(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                PlatformRelease = new PlatformReleaseOptions
                {
                    Version = "2026.07.0",
                    ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
                    Workers = [new PlatformReleaseWorkerImageOptions { RuntimeProfile = "gdal", ArtifactReference = "ghcr.io/honua/worker-gdal:2026.07.0" }]
                },
                ExecutionWorkloads =
                [
                    new ExecutionWorkloadOptions
                    {
                        WorkloadId = "gp-gdal",
                        TargetKind = global::Honua.Core.Features.ControlPlane.Domain.BatchComputeTargetKind.KubernetesJob,
                        Backend = "local",
                        WorkloadName = "GDAL GP",
                        RuntimeProfile = "gdal",
                        ArtifactReference = "ghcr.io/honua/worker-gdal:pinned"
                    }
                ]
            }));

        var gdal = await registry.GetAsync("gp-gdal");

        gdal!.ArtifactReference.Should().Be("ghcr.io/honua/worker-gdal:pinned");
    }

    private sealed class TestControlPlaneOptionsMonitor(ControlPlaneOptions currentValue) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => currentValue;

        public ControlPlaneOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }
}
