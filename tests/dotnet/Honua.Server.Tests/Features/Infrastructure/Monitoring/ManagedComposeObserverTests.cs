// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>Live target readback must never substitute desired configuration for observed state.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class ManagedComposeObserverTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_ConcurrentScopes_ShareCollectionAndRefreshAfterCompletion(bool cancelFirstCaller)
    {
        var pending = new TaskCompletionSource<ComposeExecutorResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return pending.Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            if (!pending.Task.IsCompleted)
                return Task.FromException<ComposeExecutorResponse>(new IOException("The executor is collecting the current observation."));
            return Task.FromResult(new ComposeExecutorResponse
            {
                Ok = true,
                Services = [new() { Service = "redis", Status = "Healthy", Description = "Ready" }]
            });
        });
        var monitor = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
        monitor.CurrentValue.Returns(Configuration());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(client);
        services.AddSingleton(monitor);
        services.AddSingleton(Options.Create(new ComposeExecutorOptions()));
        services.AddObservability(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        using var caller = new CancellationTokenSource();
        var first = firstScope.ServiceProvider.GetRequiredService<IManagedComposeObserver>().ReadAsync(caller.Token);
        var second = secondScope.ServiceProvider.GetRequiredService<IManagedComposeObserver>().ReadAsync(CancellationToken.None);
        if (cancelFirstCaller)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        pending.SetResult(new ComposeExecutorResponse
        {
            Ok = true,
            Services = [new() { Service = "redis", Status = "Unhealthy", Description = "Stopped" }]
        });
        if (!cancelFirstCaller)
            Assert.True(Assert.Single(await first).Available);
        var observed = Assert.Single(await second);
        Assert.True(observed.Available, "concurrent callers must share the current collection instead of timing out behind it");
        Assert.Equal("Unhealthy", observed.Status);

        var refreshed = Assert.Single(await secondScope.ServiceProvider.GetRequiredService<IManagedComposeObserver>()
            .ReadAsync(CancellationToken.None));
        Assert.Equal("Healthy", refreshed.Status);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [IntegrationTheory]
    [InlineData(15, 14)]
    [InlineData(60, 40)]
    [Trait("Tier", "Fast")]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_SlowServingProbes_DoNotHideStoppedRedis(int probeTimeoutSeconds, int responseDelaySeconds)
    {
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var probes = builder.Build();
        probes.MapGet("/{**path}", async (CancellationToken cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(responseDelaySeconds), cancellationToken);
            return Results.StatusCode(503);
        });
        await probes.StartAsync();
        var address = probes.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var configuration = Configuration();
        var image = configuration.PlatformRelease.ServingArtifactReference!;
        var root = Path.Join(Path.GetTempPath(), "honua-observe-budget", Guid.NewGuid().ToString("N"));
        var options = new ComposeExecutorOptions
        {
            SocketPath = Path.Join(root, "executor.sock"),
            ProjectDirectory = root,
            ProjectName = "slow-probe-unit",
            BackupDirectory = Path.Join(root, "backups"),
            ReleaseImage = image,
            HealthProbeUrl = address + "/healthz/ready",
            CustomerProbeUrl = address + "/healthz/ready",
            ProbeTimeoutSeconds = probeTimeoutSeconds
        };
        Directory.CreateDirectory(options.BackupDirectory);
        await File.WriteAllTextAsync(Path.Join(root, "compose.yaml"), "services: {}\n");
        Assert.Null(options.ValidateForProcess());
        var docker = Substitute.For<IDockerComposeProcess>();
        docker.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<IReadOnlyList<string>>(0);
                var output = args.Contains("ps") ? """
                    {"ID":"serving","Service":"honua","Project":"slow-probe-unit","State":"running"}
                    {"ID":"database","Service":"postgres","Project":"slow-probe-unit","State":"running"}
                    {"ID":"cache","Service":"redis","Project":"slow-probe-unit","State":"exited"}
                    """
                    : args.Contains("{{.State.StartedAt}}") ? "2026-11-01T00:00:00Z"
                    : args.Contains("{{json .RepoDigests}}") ? "[]"
                    : args.Contains("{{.Image}}") || args.Contains("{{.Id}}") ? image
                    : args.Contains("SELECT PostGIS_Version()") ? "3.5"
                    : throw new InvalidOperationException("Unexpected Docker readback command.");
                return Task.FromResult(new DockerProcessResult(0, output, string.Empty));
            });
        using var controller = new ComposeProjectController(options, docker);
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => controller.ExecuteAsync(call.ArgAt<ComposeExecutorRequest>(0), call.ArgAt<CancellationToken>(1)));

        try
        {
            var target = Assert.Single(await CreateObserver(client, configuration).ReadAsync(CancellationToken.None));
            Assert.True(target.Available, "slow serving probes must not discard the stopped dependency's readback");
            Assert.Equal("Unhealthy", Assert.Single(target.Services, service => service.Name == "redis").Status);
            Assert.True(EvidencePostureFactory.IsActionable(ManagedComposeObserver.Source([target])));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_InjectedEvaluationTime_PreservesDeterministicEvidence()
    {
        var options = Configuration();
        options.DeployTargets[0].TargetKind = DeployTargetKind.Kubernetes;
        var now = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var result = ManagedComposeObserver.ProjectRelease(options, [], evaluatedAt: now);
        var source = Assert.Single(result.EvidencePosture!.Sources);
        Assert.Equal(now, source.ObservedAt);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, source.Completeness);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Source_MixedUnknownAndUnhealthyServices_DoesNotAuthorizeAnAction()
    {
        var target = new OpsManagedTargetHealthView
        {
            TargetId = "compose-local",
            Available = true,
            Status = "Unhealthy",
            ObservedAt = DateTimeOffset.UtcNow,
            Services =
            [
                new() { Name = "honua", Status = "Unknown", DurationMs = 0 },
                new() { Name = "redis", Status = "Unhealthy", DurationMs = 0 }
            ]
        };
        Assert.False(EvidencePostureFactory.IsActionable(ManagedComposeObserver.Source([target])));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_NonComposeTarget_PreservesConfiguredEvidenceWithoutClaimingLiveReadback()
    {
        var options = Configuration();
        options.DeployTargets[0].TargetKind = DeployTargetKind.Kubernetes;
        options.DeployTargets[0].Backend = "kubernetes";
        var result = ManagedComposeObserver.ProjectRelease(options, []);
        Assert.True(result.IsCoVersioned);
        Assert.Equal("notObserved", Assert.Single(result.Serving).ObservationStatus);
        var source = Assert.Single(result.EvidencePosture!.Sources);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, source.Completeness);
        Assert.Equal(EvidencePostureVocabulary.BackendKinds.ConfigProjection, source.BackendKind);
        Assert.Equal("control-plane-options", source.BackendId);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_BeforeUpgradeAndAfterRollback_ReportsLivePriorRevisionAndSkew()
    {
        var options = Configuration();
        var live = Target("sha256:" + new string('b', 64));
        var result = ManagedComposeObserver.ProjectRelease(options, [live]);
        var serving = Assert.Single(result.Serving);
        Assert.Equal(live.RunningImage, serving.ObservedArtifactReference);
        Assert.Equal("2026.1-rc.1", serving.ServerVersion);
        Assert.Equal(options.PlatformRelease.ServingArtifactReference, serving.EffectiveArtifactReference);
        Assert.True(serving.Skewed);
        Assert.False(result.IsCoVersioned);
        Assert.Contains("compose-local", result.SkewedIds);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_MatchingLiveDigestAndVersion_ReportsConverged()
    {
        var options = Configuration();
        var result = ManagedComposeObserver.ProjectRelease(options, [Target(options.PlatformRelease.ServingArtifactReference!)]);
        Assert.True(result.IsCoVersioned);
        Assert.False(Assert.Single(result.Serving).Skewed);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_MissingReadback_DoesNotClaimConfiguredImageIsServing()
    {
        var result = ManagedComposeObserver.ProjectRelease(Configuration(), []);
        Assert.Null(Assert.Single(result.Serving).ObservedArtifactReference);
        Assert.Equal("notObserved", result.Serving[0].ObservationStatus);
        Assert.False(result.IsCoVersioned);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, result.EvidencePosture!.Sources[0].Completeness);
        Assert.Null(result.EvidencePosture.Sources[0].ObservedAt);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_ExecutorUnavailable_ReturnsUnknownWithoutInventingServiceFailures()
    {
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ComposeExecutorResponse>>(_ => throw new IOException("unavailable"));
        var observer = CreateObserver(client, Configuration());
        var target = Assert.Single(await observer.ReadAsync(CancellationToken.None));
        Assert.False(target.Available);
        Assert.Equal("Unknown", target.Status);
        Assert.Null(target.RunningImage);
        Assert.Empty(target.Services);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_UnboundLocalTarget_DoesNotReadAnotherTargetsExecutor()
    {
        var client = Substitute.For<IComposeExecutorClient>();
        var options = Configuration();
        options.DeployTargets[0].TargetId = "another-target";
        var target = Assert.Single(await CreateObserver(client, options).ReadAsync(CancellationToken.None));
        Assert.False(target.Available);
        await client.DidNotReceive().ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_StoppedService_IsCompleteActionableEvidenceOfAnOutage()
    {
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(new ComposeExecutorResponse
        {
            Ok = true,
            ReadbackComplete = false,
            Services = [new() { Service = "redis", Status = "Unhealthy", Description = "The managed service is stopped." }]
        });
        var targets = await CreateObserver(client, Configuration()).ReadAsync(CancellationToken.None);
        Assert.Equal("Unhealthy", Assert.Single(targets).Status);
        Assert.True(EvidencePostureFactory.IsActionable(ManagedComposeObserver.Source(targets)));
    }

    private static ManagedComposeObserver CreateObserver(IComposeExecutorClient client, ControlPlaneOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
        monitor.CurrentValue.Returns(options);
        return new(client, Options.Create(new ComposeExecutorOptions()), monitor, TimeProvider.System);
    }

    private static ControlPlaneOptions Configuration() => new()
    {
        PlatformRelease = new() { Version = "2026.1-rc.2", ServingArtifactReference = "sha256:" + new string('a', 64) },
        DeployTargets = [new() { TargetId = "compose-local", TargetKind = DeployTargetKind.LocalCompose,
            Backend = ComposeDeployBackend.AdapterBackendName }]
    };

    private static OpsManagedTargetHealthView Target(string image) => new()
    {
        TargetId = "compose-local",
        Available = true,
        Status = "Healthy",
        ObservedAt = DateTimeOffset.UtcNow,
        RunningImage = image,
        ImageId = image,
        ServerVersion = "2026.1-rc.1"
    };
}
