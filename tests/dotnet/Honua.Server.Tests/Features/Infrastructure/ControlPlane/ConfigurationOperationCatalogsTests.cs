// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.ControlPlane;
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

    private sealed class TestControlPlaneOptionsMonitor(ControlPlaneOptions currentValue) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => currentValue;

        public ControlPlaneOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }
}
