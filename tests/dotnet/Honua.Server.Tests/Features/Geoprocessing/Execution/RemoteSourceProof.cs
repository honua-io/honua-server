// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

internal static class RemoteSourceProof
{
    internal static async Task<JsonDocument> Execute(IServiceProvider services, string processId, params (string Name, string Value)[] inputs)
    {
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions { MaxArtifactBytes = 1024 * 1024 });
        var executor = RemoteSourceExecutor.ForProcess(processId, services.GetRequiredService<IServiceScopeFactory>(),
            options, NullLogger<RemoteSourceExecutor>.Instance);
        var parameters = new Dictionary<string, string>
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId
        };
        foreach (var (name, value) in inputs)
        {
            parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.{name}"] = value;
        }
        var job = new ExecutionJobRecord
        {
            OperationId = "remote-source-proof", Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local", WorkloadName = "geoprocessing:proof", Parameters = parameters
            }
        };
        var artifacts = new List<string>();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns(job.OperationId);
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => artifacts.Add(call.ArgAt<string>(0)));
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        artifacts.Should().ContainSingle();
        const string prefix = "data:application/geo+json;base64,";
        artifacts[0].Should().StartWith(prefix);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(artifacts[0][prefix.Length..]));
        var output = JsonDocument.Parse(decoded);
        output.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        return output;
    }
}
