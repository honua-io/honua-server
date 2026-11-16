// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

public sealed partial class FeatureLockEnforcingFeatureWriterTests
{
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task Transaction_ReceiptCommit_ForwardsCapabilityReceiptAndCancellation()
    {
        var (writer, _, inner) = CreateWriter();
        var receipt = new FeatureWriterTransactionReceipt
        {
            Scope = "native-service-edits",
            Payload = new byte[] { 1, 2, 3 },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        using var cancellation = new CancellationTokenSource();
        await using var transaction = await writer.BeginTransactionAsync();
        transaction.SupportsDurableReceipts.Should().BeTrue();
        (await transaction.CommitAsync(receipt, cancellation.Token))
            .Should().Be(FeatureWriterTransactionCommitOutcome.SupersededByExistingReceipt);
        inner.Transaction.Receipt.Should().Be(receipt);
        inner.Transaction.CommitCancellation.Should().Be(cancellation.Token);
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task Transaction_ReceiptUnsupported_DefaultDoesNotCommitUnkeyed()
    {
        await using var provider = new LegacyTransaction();
        IFeatureWriterTransaction transaction = provider;
        transaction.SupportsDurableReceipts.Should().BeFalse();
        var receipt = new FeatureWriterTransactionReceipt
        {
            Scope = "unsupported",
            Payload = new byte[] { 1 },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var commit = () => transaction.CommitAsync(receipt);
        await commit.Should().ThrowAsync<NotSupportedException>();
        provider.Committed.Should().BeFalse();
    }

    private sealed class ReceiptRecordingTransaction : IFeatureWriterTransaction
    {
        public bool SupportsDurableReceipts => true;
        public FeatureWriterTransactionReceipt? Receipt { get; private set; }
        public CancellationToken CommitCancellation { get; private set; }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(
            FeatureWriterTransactionReceipt receipt, CancellationToken cancellationToken = default)
        {
            Receipt = receipt;
            CommitCancellation = cancellationToken;
            return Task.FromResult(FeatureWriterTransactionCommitOutcome.SupersededByExistingReceipt);
        }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureWriterTransactionCommitOutcome.Committed);

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureEditResult.Success(0, 0, 0));

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LegacyTransaction : IFeatureWriterTransaction
    {
        public bool Committed { get; private set; }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
        {
            Committed = true;
            return Task.FromResult(FeatureWriterTransactionCommitOutcome.Committed);
        }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureEditResult.Success(0, 0, 0));

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
