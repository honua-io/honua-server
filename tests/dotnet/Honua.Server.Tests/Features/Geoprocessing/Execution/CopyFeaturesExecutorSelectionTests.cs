// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class CopyFeaturesExecutorSelectionTests
{
    [UnitTest]
    public async Task CopyFeatures_SelectionRejected_IsTerminalRefusalWithGuidance()
    {
        const string guidance = "the where clause is not supported; use simple comparisons joined by AND";
        var copier = Substitute.For<IFeatureLayerCopyService>();
        copier.CopyAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<FeatureQuery>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<FeatureLayerCopyResult>>(_ => throw new DagSourceSelectionException(guidance));
        using var services = new ServiceCollection().AddSingleton(copier).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new CopyFeaturesExecutor(services.GetRequiredService<IServiceScopeFactory>(),
            options, NullLogger<CopyFeaturesExecutor>.Instance);

        var result = await executor.ExecuteAsync(Job(), Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Be($"Invalid {CopyFeaturesExecutor.HandledProcessId} inputs: {guidance}");
        result.IsRetryable.Should().BeFalse("a rejected selector fails the same way on every attempt");
    }

    private static ExecutionJobRecord Job()
    {
        const string prefix = ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0.";
        return new ExecutionJobRecord
        {
            OperationId = "copy-selection",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:data-management.copy-features",
                Parameters = new Dictionary<string, string>
                {
                    ["protocolProcessId"] = CopyFeaturesExecutor.HandledProcessId,
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = CopyFeaturesExecutor.HandledProcessId,
                    [prefix + "sourceLayerId"] = "1",
                    [prefix + "targetLayerName"] = "copy",
                    [prefix + "where"] = "1=1 AND 'a' = 'a'",
                }
            }
        };
    }
}
