// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using Honua.ControlPlane;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed partial class ManagedComposeObserverTests
{
    private static readonly DateTimeOffset LogT0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [UnitTheory]
    [InlineData("socket", "IOException (SocketException ConnectionRefused)")]
    [InlineData("timeout", "TaskCanceledException")]
    [InlineData("error", "executor code observe_failed")]
    [InlineData("empty", "executor reported no services")]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_FailedReadback_LogsTargetAndFailureClassWithoutDetail(string failure, string failureClass)
    {
        var logger = EnabledLogger();
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(failure switch
        {
            "socket" => Task.FromException<ComposeExecutorResponse>(new IOException("private-host-detail",
                new SocketException((int)SocketError.ConnectionRefused))),
            "timeout" => Task.FromException<ComposeExecutorResponse>(new TaskCanceledException("private-host-detail")),
            "error" => Task.FromResult(new ComposeExecutorResponse { Ok = false, Code = "observe_failed", Message = "private-host-detail" }),
            _ => Task.FromResult(new ComposeExecutorResponse { Ok = true, Message = "private-host-detail" })
        });

        var target = Assert.Single(await CreateLoggingObserver(client, new SettableClock(LogT0), logger)
            .ReadAsync(CancellationToken.None));

        Assert.False(target.Available);
        var message = Assert.Single(LoggedMessages(logger));
        Assert.Equal($"Managed Compose target compose-local health readback failed: {failureClass}.", message);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_RepeatedReadbackFailure_LogsOncePerFiveMinutesAndAgainAfterRecovery()
    {
        var logger = EnabledLogger();
        var clock = new SettableClock(LogT0);
        var fails = true;
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(_ => fails
            ? Task.FromException<ComposeExecutorResponse>(new IOException("unavailable"))
            : Task.FromResult(new ComposeExecutorResponse
            {
                Ok = true,
                Services = [new() { Service = "honua", Status = "Healthy", Description = "Ready" }]
            }));
        var observer = CreateLoggingObserver(client, clock, logger);
        var logged = new List<int>();
        foreach (var (minute, failing) in new[] { (0, true), (1, true), (4, true), (5, true), (6, false), (7, true) })
        {
            clock.Now = LogT0.AddMinutes(minute);
            fails = failing;
            await observer.ReadAsync(CancellationToken.None);
            logged.Add(LoggedMessages(logger).Count);
        }
        Assert.Equal([1, 1, 1, 2, 2, 3], logged);
    }

    private static ManagedComposeObserver CreateLoggingObserver(
        IComposeExecutorClient client, TimeProvider clock, ILogger<ManagedComposeObserver> logger)
    {
        var monitor = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
        monitor.CurrentValue.Returns(Configuration());
        return new(new(client, new ComposeExecutorOptions()), monitor, clock, logger);
    }

    private static ILogger<ManagedComposeObserver> EnabledLogger()
    {
        var logger = Substitute.For<ILogger<ManagedComposeObserver>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return logger;
    }

    private static List<string> LoggedMessages(ILogger<ManagedComposeObserver> logger)
        => logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments()[2]?.ToString() ?? string.Empty)
            .ToList();
}
