// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Tests.Features.Infrastructure;

/// <summary>
/// Regression tests for <see cref="DbTransactionExtensions.CommitSafelyAsync"/>.
/// These tests verify the phantom-commit guard (BH3-018 / BH3-019): the method
/// must throw <see cref="OperationCanceledException"/> when the token is already
/// cancelled BEFORE touching the database, and must pass
/// <see cref="CancellationToken.None"/> to the underlying commit so the in-flight
/// round-trip is never interrupted.
/// </summary>
public sealed class DbTransactionExtensionsTests
{
    [Fact]
    public async Task CommitSafelyAsync_PreCancelledToken_ThrowsBeforeCommitIsCalled()
    {
        var wasCommitCalled = false;
        var stub = new StubDbTransaction(onCommit: _ => { wasCommitCalled = true; return Task.CompletedTask; });

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => stub.CommitSafelyAsync(cts.Token));

        Assert.False(wasCommitCalled, "CommitAsync must not be called when the token is already cancelled.");
    }

    [Fact]
    public async Task CommitSafelyAsync_ActiveToken_PassesCancellationTokenNoneToUnderlyingCommit()
    {
        CancellationToken capturedToken = default;
        var stub = new StubDbTransaction(onCommit: ct => { capturedToken = ct; return Task.CompletedTask; });

        using var cts = new CancellationTokenSource();
        await stub.CommitSafelyAsync(cts.Token);

        Assert.Equal(CancellationToken.None, capturedToken);
    }

    [Fact]
    public async Task CommitSafelyAsync_CancellationTokenNoneInput_CommitsSuccessfully()
    {
        var wasCommitted = false;
        var stub = new StubDbTransaction(onCommit: _ => { wasCommitted = true; return Task.CompletedTask; });

        // CancellationToken.None is a common call site pattern after the pre-check has
        // already been performed by the caller; verify it does not throw.
        await stub.CommitSafelyAsync(CancellationToken.None);

        Assert.True(wasCommitted);
    }

    /// <summary>Minimal concrete DbTransaction for unit-testing the extension.</summary>
    private sealed class StubDbTransaction : DbTransaction
    {
        private readonly Func<CancellationToken, Task> _onCommit;

        public StubDbTransaction(Func<CancellationToken, Task> onCommit)
        {
            _onCommit = onCommit;
        }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;

        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();

        public override Task CommitAsync(CancellationToken cancellationToken) =>
            _onCommit(cancellationToken);
    }
}
