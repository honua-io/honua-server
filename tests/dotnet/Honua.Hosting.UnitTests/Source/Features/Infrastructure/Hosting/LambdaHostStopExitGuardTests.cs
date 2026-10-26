// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Hosting;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Hosting;

/// <summary>
/// A host that starts stopping inside a Lambda sandbox must end the process, or Lambda keeps
/// routing invocations to a process whose port is closed (honua-server#6558).
/// </summary>
public sealed class LambdaHostStopExitGuardTests
{
    [UnitTest]
    public async Task ApplicationStopping_StopStallsPastGrace_ExitsWithStalledStopCode()
    {
        using var lifetime = new TestLifetime();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guard = CreateGuard(lifetime, TimeSpan.FromMilliseconds(50), code => exited.TrySetResult(code));

        await guard.StartAsync(CancellationToken.None);
        lifetime.StopApplication();
        // The guard's own StopAsync runs during the very stop it guards and must not disarm it.
        await guard.StopAsync(CancellationToken.None);

        var completed = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(exited.Task, "a stalled stop must end the process");
        (await exited.Task).Should().Be(LambdaHostStopExitGuard.StalledStopExitCode);
    }

    [UnitTest]
    public async Task RunningHost_NotStopping_DoesNotExit()
    {
        using var lifetime = new TestLifetime();
        var exits = 0;
        using var guard = CreateGuard(lifetime, TimeSpan.FromMilliseconds(20), _ => Interlocked.Increment(ref exits));

        await guard.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        exits.Should().Be(0);
    }

    [UnitTest]
    public async Task ApplicationStopping_SignalledTwice_ExitsOnce()
    {
        using var lifetime = new TestLifetime();
        var exits = 0;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guard = CreateGuard(lifetime, TimeSpan.FromMilliseconds(20), _ =>
        {
            Interlocked.Increment(ref exits);
            exited.TrySetResult();
        });

        await guard.StartAsync(CancellationToken.None);
        lifetime.StopApplication();
        lifetime.StopApplication();
        await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        exits.Should().Be(1);
    }

    [UnitTheory]
    [InlineData("honua-demo", null, true)]
    [InlineData(null, "127.0.0.1:9001", true)]
    [InlineData(null, null, false)]
    [InlineData(" ", "", false)]
    public void IsAwsLambda_ReadsLambdaRuntimeVariables(string? functionName, string? runtimeApi, bool expected)
    {
        var environment = new Dictionary<string, string?>
        {
            [LambdaHostStopExitGuard.AwsLambdaFunctionNameKey] = functionName,
            [LambdaHostStopExitGuard.AwsLambdaRuntimeApiKey] = runtimeApi,
        };

        LambdaHostStopExitGuard.IsAwsLambda(key => environment.GetValueOrDefault(key)).Should().Be(expected);
    }

    private static LambdaHostStopExitGuard CreateGuard(TestLifetime lifetime, TimeSpan grace, Action<int> exit)
        => new(lifetime, NullLogger<LambdaHostStopExitGuard>.Instance, TimeProvider.System, grace, exit);

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
