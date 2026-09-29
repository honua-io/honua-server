// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class RoutingJobExecutorTests
{
    [UnitTheory]
    [InlineData("{\"stops\":[]}")]
    [InlineData("{\"stops\":null}")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"travelMode\":\"flying\"}")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"inSrid\":0}")]
    [InlineData("invalid json")]
    public async Task CanonicalRequest_InvalidRoute_DoesNotInvokeProviderOrPublish(string request)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();

        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route, request), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task CanonicalRequest_StopLimit_IsEnforcedAtWorker()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().Configure<RoutingConfiguration>(value => value.MaxStops = 2)
            .AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var request = new RouteSolveRequest([new(1, 1), new(2, 2), new(3, 3)]);
        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route,
            JsonSerializer.Serialize(request, RoutingJobJsonContext.Default.RouteSolveRequest)), Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
    }

    [UnitTest]
    public async Task CanonicalRequest_NoRoute_PublishesFalseSolveResult()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>()).Returns(new RouteSolveResult("", 0, 0, []));
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var outputs = new List<string>();
        context.When(value => value.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(call => outputs.Add(call.ArgAt<string>(0)));
        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route,
            JsonSerializer.Serialize(new RouteSolveRequest([new(1, 1), new(2, 2)]), RoutingJobJsonContext.Default.RouteSolveRequest)), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        outputs.Should().HaveCount(2);
        using var features = JsonDocument.Parse(Convert.FromBase64String(outputs[0].Split(',')[1]));
        features.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
        using var solved = JsonDocument.Parse(Convert.FromBase64String(outputs[1].Split(',')[1]));
        solved.RootElement.GetBoolean().Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("[]")]
    [InlineData("[0]")]
    [InlineData("[5,2]")]
    [InlineData("[2,2]")]
    public async Task CanonicalRequest_InvalidServiceAreaBreaks_DoesNotInvokeProviderOrPublish(string breaks)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var request = "{\"facilities\":[{\"lon\":1,\"lat\":1}],\"breaks\":" + breaks + "}";

        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.ServiceArea, request), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("breaks");
        result.IsRetryable.Should().BeFalse();
        await provider.DidNotReceiveWithAnyArgs().SolveServiceAreaAsync(default!, default);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task CanonicalRequest_CanceledAfterProviderReturns_PublishesNoArtifacts()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return new RouteSolveResult("{\"type\":\"LineString\",\"coordinates\":[[1,1],[2,2]]}", 1, 1, []);
        });
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var request = JsonSerializer.Serialize(new RouteSolveRequest([new(1, 1), new(2, 2)]), RoutingJobJsonContext.Default.RouteSolveRequest);

        var execute = () => executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route, request), context, cancellation.Token);

        await execute.Should().ThrowAsync<OperationCanceledException>();
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    private static ExecutionJobRecord Job(string processId, string request) => new()
    {
        OperationId = "routing-worker-test",
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "geoprocessing:routing",
            Parameters = new Dictionary<string, string>
            {
                [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
                [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.request"] = request,
            },
        },
    };
}
