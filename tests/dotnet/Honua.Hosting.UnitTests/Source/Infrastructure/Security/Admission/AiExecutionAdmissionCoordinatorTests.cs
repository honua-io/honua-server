// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security.Admission;

public sealed class AiExecutionAdmissionCoordinatorTests
{
    private static AiExecutionContext Context => AiExecutionContext.Create("deployment", "apikey:api-key:actor", "studio-chat");

    [UnitTest]
    public async Task AcquireAsync_DeniedOrAmbiguousStore_DoesNotRetryOrCreatePermit()
    {
        var store = new Store { Code = "budget_exceeded", IncludeReservation = false };
        using var services = Services(store);
        var admission = Coordinator(services);
        await FluentActions.Awaiting(() => admission.AcquireAsync(Context, default)).Should().ThrowAsync<AiAdmissionException>();
        store.Acquires.Should().Be(1);
        store.ThrowAcquire = true;
        await FluentActions.Awaiting(() => admission.AcquireAsync(Context, default)).Should().ThrowAsync<AiAdmissionException>();
        store.Acquires.Should().Be(2, "each caller attempt performs one acquire, never an ambiguous retry");
    }

    [UnitTest]
    public async Task AcquireAsync_NonAdmittedCodeWithReservation_IsRejected()
    {
        var store = new Store { Code = "stopped" };
        using var services = Services(store);
        await FluentActions.Awaiting(() => Coordinator(services).AcquireAsync(Context, default)).Should().ThrowAsync<AiAdmissionException>();
    }

    [UnitTest]
    public async Task AcquireAsync_WrongDeployment_RejectsBeforeStore()
    {
        var store = new Store();
        using var services = Services(store);
        await FluentActions.Awaiting(() => Coordinator(services).AcquireAsync(Context with { DeploymentId = "other" }, default))
            .Should().ThrowAsync<AiAdmissionException>();
        store.Acquires.Should().Be(0);
    }

    [UnitTest]
    public async Task AuthorizeSendAsync_OnePermit_CannotAuthorizeAnotherSend()
    {
        var store = new Store();
        using var services = Services(store);
        await using var permit = await Coordinator(services).AcquireAsync(Context, default);
        await permit.BeginInvocationAsync(default);
        await permit.AuthorizeSendAsync(default);
        await FluentActions.Awaiting(() => permit.AuthorizeSendAsync(default)).Should().ThrowAsync<AiAdmissionException>();
        store.Acquires.Should().Be(1);
    }

    [UnitTest]
    public async Task EnsureCurrentAsync_CallerCancelsDuringTrueStoreReply_DoesNotRevivePermit()
    {
        var store = new Store { CheckReply = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var services = Services(store);
        using var caller = new CancellationTokenSource();
        await using var permit = await Coordinator(services).AcquireAsync(Context, caller.Token);
        var begin = permit.BeginInvocationAsync(default);
        await store.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await caller.CancelAsync();
        store.CheckReply.SetResult(true);
        await FluentActions.Awaiting(() => begin).Should().ThrowAsync<OperationCanceledException>();
        permit.Token.IsCancellationRequested.Should().BeTrue();
    }

    [UnitTest]
    public async Task StopOrOutage_AuthoritativePoll_CancelsLivePermit()
    {
        var store = new Store();
        using var services = Services(store);
        await using var permit = await Coordinator(services).AcquireAsync(Context, default);
        await permit.BeginInvocationAsync(default);
        store.Current = false;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = permit.Token.Register(() => stopped.TrySetResult());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await FluentActions.Awaiting(() => permit.AuthorizeSendAsync(default)).Should().ThrowAsync<AiAdmissionException>();
    }

    [UnitTest]
    public async Task Deadline_FixedGrant_CancelsEvenIfStoreKeepsReturningTrue()
    {
        var store = new Store { Lifetime = TimeSpan.FromMilliseconds(150) };
        using var services = Services(store);
        await using var permit = await Coordinator(services).AcquireAsync(Context, default);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = permit.Token.Register(() => expired.TrySetResult());
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await FluentActions.Awaiting(() => permit.BeginInvocationAsync(default)).Should().ThrowAsync<AiAdmissionException>();
    }

    [UnitTest]
    public async Task DisposeAsync_CallerAlreadyAborted_ReleasesExactlyOnceWithIndependentToken()
    {
        var store = new Store();
        using var services = Services(store);
        using var caller = new CancellationTokenSource();
        var permit = await Coordinator(services).AcquireAsync(Context, caller.Token);
        await caller.CancelAsync();
        await permit.DisposeAsync();
        await permit.DisposeAsync();
        store.Releases.Should().Be(1);
        store.ReleaseWasCancelled.Should().BeFalse();
        store.Acquires.Should().Be(1, "releasing capacity never refunds or reacquires an attempt");
    }

    private static ServiceProvider Services(Store store) => new ServiceCollection()
        .AddScoped<IAiExecutionAdmissionStore>(_ => store).BuildServiceProvider();
    private static AiExecutionAdmissionCoordinator Coordinator(IServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AiExecutionAdmissionOptions { DeploymentId = "deployment" }),
        TimeProvider.System, NullLogger<AiExecutionAdmissionCoordinator>.Instance);

    private sealed class Store : IAiExecutionAdmissionStore
    {
        public int Acquires;
        public int Releases;
        public bool ReleaseWasCancelled;
        public bool Current = true;
        public bool IncludeReservation = true;
        public bool ThrowAcquire;
        public string Code = "admitted";
        public TimeSpan Lifetime = TimeSpan.FromSeconds(30);
        public TaskCompletionSource<bool>? CheckReply;
        public TaskCompletionSource CheckEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, CancellationToken cancellationToken)
        {
            Acquires++;
            if (ThrowAcquire) throw new InvalidOperationException("ambiguous commit");
            var deadline = DateTimeOffset.UtcNow + Lifetime;
            return Task.FromResult(new AiAdmissionDecision(Code, IncludeReservation
                ? new(context.DeploymentId, attemptId, Guid.NewGuid(), 1, deadline, deadline.AddSeconds(30)) : null));
        }
        public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest request, CancellationToken cancellationToken)
            => TryAcquireAsync(context, attemptId, cancellationToken);
        public Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken cancellationToken) => Task.FromResult("unavailable");
        public Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
        {
            CheckEntered.TrySetResult();
            return CheckReply?.Task ?? Task.FromResult(Current);
        }
        public Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
        {
            Releases++;
            ReleaseWasCancelled = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
        public Task<AiAdmissionState?> GetStateAsync(string deploymentId, CancellationToken cancellationToken) => Task.FromResult<AiAdmissionState?>(null);
        public Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult(new AiAdmissionControlResult("unavailable", null));
    }
}
