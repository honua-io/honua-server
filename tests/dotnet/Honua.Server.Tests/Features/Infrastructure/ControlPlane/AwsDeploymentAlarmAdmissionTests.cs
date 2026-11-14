// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsDeploymentAlarmAdmissionTests
{
    private const string TargetId = "prod-ecs";

    [Fact]
    public async Task GetBlockingReasonAsync_Disabled_DoesNotReadProvider()
    {
        var client = Substitute.For<IAwsDeploymentAlarmClient>();
        var guard = new AwsDeploymentAlarmAdmission(Options.Create(new AwsDeploymentAlarmOptions()), client);

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().BeNull();
        client.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("ALARM")]
    [InlineData("INSUFFICIENT_DATA")]
    [InlineData("")]
    [InlineData("ok")]
    public async Task GetBlockingReasonAsync_NonOkState_Blocks(string state)
    {
        var (guard, _) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", state)));

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetBlockingReasonAsync_AllRegionsOk_Allows()
    {
        var (guard, client) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));
        client.ReadAsync("us-east-1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Snapshot(new AwsDeploymentAlarmState("edge", "OK")));
        var options = ConfiguredOptions();
        options.Targets[TargetId].Regions.Add("us-east-1", ["edge"]);
        guard = new AwsDeploymentAlarmAdmission(Options.Create(options), client);

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().BeNull();
        await client.Received(1).ReadAsync("us-west-2", Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "regional" })), Arg.Any<CancellationToken>());
        await client.Received(1).ReadAsync("us-east-1", Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "edge" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBlockingReasonAsync_UnknownTarget_BlocksWithoutProviderCall()
    {
        var (guard, client) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));

        (await guard.GetBlockingReasonAsync("other", CancellationToken.None)).Should().NotBeNull();
        client.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GetBlockingReasonAsync_IncompleteOrMissingOrDuplicateAlarm_Blocks(bool missing, bool duplicate)
    {
        var alarms = missing ? Array.Empty<AwsDeploymentAlarmState>() : duplicate
            ? new[] { new AwsDeploymentAlarmState("regional", "OK"), new("regional", "OK") }
            : new[] { new AwsDeploymentAlarmState("regional", "OK") };
        var (guard, _) = CreateGuard(new AwsDeploymentAlarmSnapshot { Alarms = alarms, IsComplete = missing || duplicate });

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetBlockingReasonAsync_ProviderFailure_BlocksWithoutExceptionDetails()
    {
        var (guard, client) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));
        client.ReadAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<AwsDeploymentAlarmSnapshot>>(_ => throw new InvalidOperationException("private-provider-detail"));

        var reason = await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None);

        reason.Should().NotBeNull().And.NotContain("private-provider-detail");
    }

    [Fact]
    public async Task GetBlockingReasonAsync_StateChangesAfterPlanning_ReadsAgain()
    {
        var (guard, client) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));
        client.ReadAsync("us-west-2", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Snapshot(new AwsDeploymentAlarmState("regional", "OK")), Snapshot(new AwsDeploymentAlarmState("regional", "ALARM")));

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().BeNull();
        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetBlockingReasonAsync_CallerCancelled_PropagatesCancellation()
    {
        var (guard, _) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => guard.GetBlockingReasonAsync(TargetId, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetBlockingReasonAsync_ConfigurationMutates_UsesStartupSnapshot()
    {
        var (_, client) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));
        var options = ConfiguredOptions();
        var guard = new AwsDeploymentAlarmAdmission(Options.Create(options), client);
        options.Enabled = false;
        options.Targets[TargetId].Regions["us-west-2"][0] = "untrusted-replacement";
        options.Targets.Clear();

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().BeNull();
        await client.Received(1).ReadAsync("us-west-2", Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "regional" })), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetBlockingReasonAsync_EmptyOrDuplicateConfiguration_BlocksWithoutProviderCalls(bool duplicate)
    {
        var (_, client) = CreateGuard(Snapshot(new AwsDeploymentAlarmState("regional", "OK")));
        var options = ConfiguredOptions();
        options.Targets[TargetId].Regions["us-west-2"] = duplicate ? ["regional", "regional"] : [];
        var guard = new AwsDeploymentAlarmAdmission(Options.Create(options), client);

        (await guard.GetBlockingReasonAsync(TargetId, CancellationToken.None)).Should().NotBeNull();
        client.ReceivedCalls().Should().BeEmpty();
    }

    private static (AwsDeploymentAlarmAdmission Guard, IAwsDeploymentAlarmClient Client) CreateGuard(AwsDeploymentAlarmSnapshot snapshot)
    {
        var client = Substitute.For<IAwsDeploymentAlarmClient>();
        client.ReadAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        return (new AwsDeploymentAlarmAdmission(Options.Create(ConfiguredOptions()), client), client);
    }

    private static AwsDeploymentAlarmOptions ConfiguredOptions() => new()
    {
        Enabled = true,
        Targets = new(StringComparer.Ordinal)
        {
            [TargetId] = new() { Regions = new(StringComparer.Ordinal) { ["us-west-2"] = ["regional"] } }
        }
    };

    private static AwsDeploymentAlarmSnapshot Snapshot(params AwsDeploymentAlarmState[] alarms)
        => new() { Alarms = alarms, IsComplete = true };
}
