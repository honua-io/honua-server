// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.AuditLog.Export;

/// <summary>
/// Unit tests for <see cref="AuditExportDispatcher"/> retry/backoff, dead-letter,
/// and residency behavior (#2157).
/// </summary>
public sealed class AuditExportDispatcherTests
{
    private readonly InMemoryAuditDeadLetterStore _deadLetters = new();

    private AuditExportDispatcher CreateDispatcher(
        AuditExportDispatcherOptions? options = null,
        AuditResidencyGuard? guard = null)
        => new(
            options ?? new AuditExportDispatcherOptions { MaxRetries = 3, BaseDelay = TimeSpan.FromMilliseconds(1) },
            _deadLetters,
            NullLogger<AuditExportDispatcher>.Instance,
            guard)
        {
            DelayAsync = static (_, _) => Task.CompletedTask,
        };

    [Fact]
    public async Task DispatchAsync_TransientThenSuccess_RetriesAndSucceeds()
    {
        var sink = new FakeAuditSink(
            region: null,
            AuditSinkResult.TransientFailure("503"),
            AuditSinkResult.Success());
        var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchAsync(sink, AuditExportTestData.Batch(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        sink.CallCount.Should().Be(2);
        var deadLettered = await _deadLetters.ListAsync(CancellationToken.None);
        deadLettered.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_TransientExhaustsRetries_DeadLettersFullBatchAndFailsPermanently()
    {
        var sink = new FakeAuditSink(region: null, AuditSinkResult.TransientFailure("503"));
        var dispatcher = CreateDispatcher(
            new AuditExportDispatcherOptions { MaxRetries = 2, BaseDelay = TimeSpan.FromMilliseconds(1) });
        var batch = AuditExportTestData.Batch(3);

        var result = await dispatcher.DispatchAsync(sink, batch, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the dispatcher converts an exhausted retry into a permanent failure");
        sink.CallCount.Should().Be(3, "initial attempt plus 2 retries");

        var deadLettered = await _deadLetters.ListAsync(CancellationToken.None);
        var batchRecord = deadLettered.Should().ContainSingle().Subject;
        batchRecord.SinkType.Should().Be("fake");
        batchRecord.Events.Should().HaveCount(3, "the full tamper-evident batch must be preserved");
    }

    [Fact]
    public async Task DispatchAsync_PermanentFailure_DeadLettersWithoutRetrying()
    {
        var sink = new FakeAuditSink(region: null, AuditSinkResult.PermanentFailure("400"));
        var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchAsync(sink, AuditExportTestData.Batch(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        sink.CallCount.Should().Be(1, "a permanent failure is never retried");

        var deadLettered = await _deadLetters.ListAsync(CancellationToken.None);
        deadLettered.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchAsync_ResidencyViolation_DeadLettersAndNeverCallsSink()
    {
        var guard = new AuditResidencyGuard(new[] { "us-east-1" });
        var sink = new FakeAuditSink(region: "eu-west-1", AuditSinkResult.Success());
        var dispatcher = CreateDispatcher(guard: guard);

        var result = await dispatcher.DispatchAsync(sink, AuditExportTestData.Batch(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        sink.CallCount.Should().Be(0, "a residency violation must block delivery before the sink is invoked");

        var deadLettered = await _deadLetters.ListAsync(CancellationToken.None);
        var batchRecord = deadLettered.Should().ContainSingle().Subject;
        batchRecord.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task DispatchAsync_EmptyBatch_ShortCircuitsToSuccess()
    {
        var sink = new FakeAuditSink(region: null, AuditSinkResult.PermanentFailure("400"));
        var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchAsync(sink, Array.Empty<Honua.Core.Features.AuditLog.Abstractions.AuditEvent>(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        sink.CallCount.Should().Be(0);
    }
}
