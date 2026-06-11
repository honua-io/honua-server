// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Compare-and-set semantics of <see cref="UniversalProgressStore"/> in its in-memory mode
/// (no distributed cache configured), the atomicity baseline for honua-server#1593.
/// </summary>
[Collection("Unit")]
public sealed class UniversalProgressStoreCompareAndSetTests
{
    private static UniversalProgressStore CreateInMemoryStore()
        => new(null, NullLogger<UniversalProgressStore>.Instance);

    private static UploadProgress CreateProgress(string uploadId, OperationStatus status)
        => UploadProgress.CreateInitial(uploadId, "data.bin", 100) with { Status = status };

    [UnitTest]
    public async Task TrySetProgressAsync_WhenStatusMatches_UpdatesProgress()
    {
        var store = CreateInMemoryStore();
        await store.SetProgressAsync("op-1", CreateProgress("op-1", OperationStatus.Processing));

        var result = await store.TrySetProgressAsync(
            "op-1", CreateProgress("op-1", OperationStatus.Cancelled), OperationStatus.Processing);

        result.Outcome.Should().Be(ProgressCompareAndSetOutcome.Updated);
        var stored = await store.GetProgressAsync("op-1");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(OperationStatus.Cancelled);
    }

    [UnitTest]
    public async Task TrySetProgressAsync_WhenWorkerCompletedFirst_DoesNotOverwriteTerminalState()
    {
        var store = CreateInMemoryStore();
        await store.SetProgressAsync("op-1", CreateProgress("op-1", OperationStatus.Completed));

        var result = await store.TrySetProgressAsync(
            "op-1", CreateProgress("op-1", OperationStatus.Cancelled), OperationStatus.Processing);

        result.Outcome.Should().Be(ProgressCompareAndSetOutcome.StatusMismatch);
        result.CurrentProgress.Should().NotBeNull();
        result.CurrentProgress!.Status.Should().Be(OperationStatus.Completed);

        var stored = await store.GetProgressAsync("op-1");
        stored!.Status.Should().Be(OperationStatus.Completed, "a rejected compare-and-set must not write");
    }

    [UnitTest]
    public async Task TrySetProgressAsync_WhenOperationMissing_ReturnsNotFound()
    {
        var store = CreateInMemoryStore();

        var result = await store.TrySetProgressAsync(
            "missing", CreateProgress("missing", OperationStatus.Cancelled), OperationStatus.Processing);

        result.Outcome.Should().Be(ProgressCompareAndSetOutcome.NotFound);
        result.CurrentProgress.Should().BeNull();
    }

    [UnitTest]
    public async Task TrySetProgressAsync_WhenEntryExpired_ReturnsNotFound()
    {
        var store = CreateInMemoryStore();
        await store.SetProgressAsync(
            "op-1", CreateProgress("op-1", OperationStatus.Processing), TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var result = await store.TrySetProgressAsync(
            "op-1", CreateProgress("op-1", OperationStatus.Cancelled), OperationStatus.Processing);

        result.Outcome.Should().Be(ProgressCompareAndSetOutcome.NotFound);
    }

    [UnitTest]
    public async Task TrySetProgressAsync_UnderConcurrentTerminalWriters_AllowsExactlyOneWinner()
    {
        var store = CreateInMemoryStore();
        await store.SetProgressAsync("op-1", CreateProgress("op-1", OperationStatus.Processing));

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            store.TrySetProgressAsync(
                "op-1",
                CreateProgress("op-1", i % 2 == 0 ? OperationStatus.Completed : OperationStatus.Cancelled),
                OperationStatus.Processing))));

        results.Count(r => r.Outcome == ProgressCompareAndSetOutcome.Updated)
            .Should().Be(1, "compare-and-set must admit exactly one terminal transition from Processing");
        results.Where(r => r.Outcome != ProgressCompareAndSetOutcome.Updated)
            .Should().OnlyContain(r => r.Outcome == ProgressCompareAndSetOutcome.StatusMismatch);

        var stored = await store.GetProgressAsync("op-1");
        (stored!.Status is OperationStatus.Completed or OperationStatus.Cancelled).Should().BeTrue();
    }

    [UnitTest]
    public async Task TrySetProgressAsync_AfterConditionalCancel_PlainTerminalWriteStillWins()
    {
        // A worker that re-asserts its terminal state after a cancel is an intentional overwrite
        // (it owns the truth about whether the work finished); the CAS only prevents the inverse
        // ordering where a cancel silently clobbers an already-persisted terminal state.
        var store = CreateInMemoryStore();
        await store.SetProgressAsync("op-1", CreateProgress("op-1", OperationStatus.Processing));

        var cancel = await store.TrySetProgressAsync(
            "op-1", CreateProgress("op-1", OperationStatus.Cancelled), OperationStatus.Processing);
        cancel.Outcome.Should().Be(ProgressCompareAndSetOutcome.Updated);

        await store.SetProgressAsync("op-1", CreateProgress("op-1", OperationStatus.Completed));

        var stored = await store.GetProgressAsync("op-1");
        stored!.Status.Should().Be(OperationStatus.Completed);
    }
}
