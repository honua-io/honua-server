// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Covers <see cref="ObservationStreamSessionManager.Dispose"/>'s Redis-unsubscribe
/// cleanup path (PA-115): it previously used a bare <c>catch { }</c> that hid any
/// exception thrown by <c>ISubscriber.Unsubscribe</c>. The fix logs a warning (with
/// the exception attached) and still swallows it, since <c>Dispose()</c> must not
/// throw.
/// </summary>
public sealed class ObservationStreamSessionManagerDisposeTests
{
    [UnitTest]
    public void Dispose_WhenUnsubscribeThrows_LogsAndDoesNotThrow()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetSubscriber().Returns(subscriber);

        var thrown = new RedisConnectionException(ConnectionFailureType.SocketFailure, "unsubscribe failed");
        subscriber
            .When(x => x.Unsubscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw thrown);

        var logger = new CapturingLogger<ObservationStreamSessionManager>();
        var manager = new ObservationStreamSessionManager(logger, redis);

        var caught = Record.Exception(manager.Dispose);

        Assert.Null(caught);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Same(thrown, entry.Exception);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
