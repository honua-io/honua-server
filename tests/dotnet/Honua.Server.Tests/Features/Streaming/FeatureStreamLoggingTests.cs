// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureStreamLoggingTests
{
    [UnitTest]
    public void ReplayStarted_ThousandIdleSubscriptionsAcrossSweeps_EmitsNoInformationRecords()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(call => call.Arg<LogLevel>() >= LogLevel.Information);
        var sessions = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();

        // Ten one-second sweeps at the declared subscription envelope would
        // previously write 10,000 Information records, despite zero events.
        for (var sweep = 0; sweep < 10; sweep++)
        {
            foreach (var session in sessions)
            {
                FeatureStreamLog.ReplayStarted(logger, 0, 0, session);
            }
        }

        Assert.DoesNotContain(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [Trait("Tier", "Fast")]
    public void ReplayStarted_DebugEnabled_PreservesStructuredDiagnostics(int count)
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sessionId = Guid.NewGuid();

        FeatureStreamLog.ReplayStarted(logger, count, 42, sessionId);

        var record = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
        var arguments = record.GetArguments();
        Assert.Equal(LogLevel.Debug, arguments[0]);
        Assert.Equal(5005, Assert.IsType<EventId>(arguments[1]).Id);
        var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(arguments[2])
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(count, fields["Count"]);
        Assert.Equal(42L, fields["Cursor"]);
        Assert.Equal(sessionId, fields["SessionId"]);
    }
}
