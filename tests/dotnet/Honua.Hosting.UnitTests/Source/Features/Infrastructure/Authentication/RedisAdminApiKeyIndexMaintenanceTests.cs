// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// The admin API-key hash-index backfill must never stop the serving host (honua-server#6558).
/// After a Lambda freeze/thaw StackExchange.Redis abandons in-flight commands as
/// <see cref="TaskCanceledException"/>. That exception escaped the loop, and
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> took the demo cell's process down.
/// </summary>
public sealed class RedisAdminApiKeyIndexMaintenanceTests
{
    public static TheoryData<string> RedisFailures => new()
    {
        nameof(TaskCanceledException),
        nameof(ObjectDisposedException),
        nameof(InvalidOperationException),
        nameof(RedisConnectionException),
    };

    [UnitTheory]
    [MemberData(nameof(RedisFailures))]
    public async Task ExecuteAsync_RedisCommandFailsOutsideShutdown_KeepsHostRunningAndRetries(string failure)
    {
        var database = Substitute.For<IDatabase>();
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        database.SetCombineAsync(Arg.Any<SetOperation>(), Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) >= 2)
                {
                    secondCall.TrySetResult();
                }

                return Fail(failure);
            });
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options =>
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
                services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
                services.AddSingleton<IAdminApiKeyStore>(new RedisAdminApiKeyStore(redis));
                services.AddHostedService(sp => new RedisAdminApiKeyIndexMaintenance(
                    sp.GetRequiredService<IAdminApiKeyStore>(),
                    NullLogger<RedisAdminApiKeyIndexMaintenance>.Instance,
                    TimeSpan.FromMilliseconds(10)));
            })
            .Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();
        var retried = await Task.WhenAny(secondCall.Task, Task.Delay(TimeSpan.FromSeconds(10))) == secondCall.Task;

        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse(
            "a failed backfill tick must not stop the serving host");
        retried.Should().BeTrue("the loop retries on the next tick");
        var maintenance = host.Services.GetServices<IHostedService>().OfType<RedisAdminApiKeyIndexMaintenance>().Single();
        maintenance.ExecuteTask.Should().NotBeNull();
        maintenance.ExecuteTask!.IsCompleted.Should().BeFalse();

        await host.StopAsync();
        maintenance.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue("shutdown still ends the loop cleanly");
    }

    private static Task<RedisValue[]> Fail(string failure) => failure switch
    {
        nameof(TaskCanceledException) => Task.FromCanceled<RedisValue[]>(new CancellationToken(canceled: true)),
        nameof(ObjectDisposedException) => Task.FromException<RedisValue[]>(new ObjectDisposedException("SafeWaitHandle")),
        nameof(InvalidOperationException) => Task.FromException<RedisValue[]>(new InvalidOperationException("No connection")),
        _ => Task.FromException<RedisValue[]>(
            new RedisConnectionException(ConnectionFailureType.UnableToConnect, "ConnectTimeout")),
    };
}
