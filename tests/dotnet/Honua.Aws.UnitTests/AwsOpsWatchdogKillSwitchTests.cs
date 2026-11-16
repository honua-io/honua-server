// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Honua.Cloud.Aws.Features.Monitoring;
using Honua.TestKit.Attributes;
using Honua.Core.Features.Observability.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Aws.UnitTests;

public sealed class AwsOpsWatchdogKillSwitchTests
{
    private const string ParameterName = "/honua/example/watchdog/mode";

    [UnitTest]
    public async Task SuspendAsync_UsesOnlyConfiguredParameterAndDisablesActions()
    {
        var requests = new List<PutParameterRequest>();
        var watchdog = Watchdog(requests, "Act");

        await watchdog.SuspendAsync(CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal(ParameterName, request.Name);
        Assert.Equal("Off", request.Value);
        Assert.Equal(ParameterType.String, request.Type);
        Assert.True(request.Overwrite);
    }

    [UnitTheory]
    [InlineData("Act")]
    [InlineData("NotifyOnly")]
    [InlineData("Off")]
    public async Task ResumeAsync_RestoresConfiguredMode(string resumeMode)
    {
        var requests = new List<PutParameterRequest>();
        var watchdog = Watchdog(requests, resumeMode);

        await watchdog.ResumeAsync(CancellationToken.None);

        Assert.Equal(resumeMode, Assert.Single(requests).Value);
    }

    [UnitTest]
    public async Task SuspendAsync_ParameterWriteFails_ReportsFailure()
    {
        var watchdog = new AwsOpsWatchdogKillSwitch(
            (_, _) => throw new AmazonSimpleSystemsManagementException("denied"),
            new AwsOpsWatchdogMode(ParameterName, "Act"));

        await Assert.ThrowsAsync<IOException>(() => watchdog.SuspendAsync(CancellationToken.None));
    }

    [UnitTheory]
    [InlineData("client")]
    [InlineData("service")]
    [InlineData("sdk-timeout")]
    public async Task SuspendAsync_UnexpectedSdkFailure_ReportsFailure(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "client" => new AmazonClientException("credentials unavailable"),
            "service" => new AmazonServiceException("throttled"),
            _ => new TaskCanceledException("request timed out"),
        };
        var watchdog = new AwsOpsWatchdogKillSwitch(
            (_, _) => throw failure,
            new AwsOpsWatchdogMode(ParameterName, "Act"));

        var reported = await Assert.ThrowsAsync<IOException>(() => watchdog.SuspendAsync(CancellationToken.None));
        Assert.Same(failure, reported.InnerException);
    }

    [UnitTest]
    public async Task SuspendAsync_CallerCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var watchdog = new AwsOpsWatchdogKillSwitch(
            (_, token) => Task.FromCanceled<PutParameterResponse>(token),
            new AwsOpsWatchdogMode(ParameterName, "Act"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchdog.SuspendAsync(cancellation.Token));
    }

    [UnitTest]
    public void AddAwsOpsWatchdogKillSwitch_NoModeParameter_DoesNotRegisterClientOrSwitch()
    {
        var services = new ServiceCollection();
        services.AddAwsOpsWatchdogKillSwitch(new ConfigurationBuilder().Build());
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IOpsWatchdogKillSwitch));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IAmazonSimpleSystemsManagement));
    }

    [UnitTest]
    public void AddAwsOpsWatchdogKillSwitch_InvalidResumeMode_RejectsConfiguration()
    {
        var configuration = Configuration("Unknown");
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAwsOpsWatchdogKillSwitch(configuration));
    }

    [UnitTest]
    public void AddAwsOpsWatchdogKillSwitch_ConfiguredParameter_RegistersControl()
    {
        var services = new ServiceCollection();
        services.AddAwsOpsWatchdogKillSwitch(Configuration("NotifyOnly"));
        Assert.Contains(services, item => item.ServiceType == typeof(IOpsWatchdogKillSwitch));
        Assert.Contains(services, item => item.ServiceType == typeof(IAmazonSimpleSystemsManagement));
    }

    private static IConfiguration Configuration(string resumeMode) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:OpsFindings:Watchdog:ModeParameterName"] = ParameterName,
            ["Observability:OpsFindings:Watchdog:ResumeMode"] = resumeMode,
        }).Build();

    private static AwsOpsWatchdogKillSwitch Watchdog(List<PutParameterRequest> requests, string resumeMode) => new(
        (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new PutParameterResponse());
        }, new AwsOpsWatchdogMode(ParameterName, resumeMode));
}
