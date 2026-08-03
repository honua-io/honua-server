// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Postgres.Features.FeatureStore.Services;
using Npgsql;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureDataAccessCommitClassificationTests
{
    [Fact]
    public async Task CommitEditTransactionAsync_ServerRejectedCommit_PropagatesConfirmedFailure()
    {
        var serverRejection = DeferredConstraintViolation();
        var transaction = new StubDbTransaction(_ => Task.FromException(serverRejection));

        var thrown = await Assert.ThrowsAsync<PostgresException>(
            () => FeatureDataAccess.CommitEditTransactionAsync(transaction, CancellationToken.None));

        thrown.Should().BeSameAs(serverRejection);
    }

    [Fact]
    public async Task CommitEditTransactionAsync_FatalAdminShutdown_WrapsAmbiguousAcknowledgement()
    {
        var fatalShutdown = PostgresFailure(
            "terminating connection due to administrator command",
            "FATAL",
            PostgresErrorCodes.AdminShutdown);
        var transaction = new StubDbTransaction(_ => Task.FromException(fatalShutdown));

        var thrown = await Assert.ThrowsAsync<FeatureEditCommitOutcomeUnknownException>(
            () => FeatureDataAccess.CommitEditTransactionAsync(transaction, CancellationToken.None));

        thrown.InnerException.Should().BeSameAs(fatalShutdown);
    }

    [Fact]
    public async Task CommitEditTransactionAsync_TransactionResolutionUnknown_WrapsAmbiguousAcknowledgement()
    {
        var unknownResolution = PostgresFailure(
            "transaction resolution is unknown",
            "ERROR",
            PostgresErrorCodes.TransactionResolutionUnknown);
        var transaction = new StubDbTransaction(_ => Task.FromException(unknownResolution));

        var thrown = await Assert.ThrowsAsync<FeatureEditCommitOutcomeUnknownException>(
            () => FeatureDataAccess.CommitEditTransactionAsync(transaction, CancellationToken.None));

        thrown.InnerException.Should().BeSameAs(unknownResolution);
    }

    [Fact]
    public async Task CommitEditTransactionAsync_ConnectionFailure_WrapsAmbiguousAcknowledgement()
    {
        var connectionFailure = new NpgsqlException("Connection was lost while awaiting COMMIT.");
        var transaction = new StubDbTransaction(_ => Task.FromException(connectionFailure));

        var thrown = await Assert.ThrowsAsync<FeatureEditCommitOutcomeUnknownException>(
            () => FeatureDataAccess.CommitEditTransactionAsync(transaction, CancellationToken.None));

        thrown.InnerException.Should().BeSameAs(connectionFailure);
    }

    private static PostgresException DeferredConstraintViolation() =>
        PostgresFailure(
            "check constraint was violated at commit",
            "ERROR",
            PostgresErrorCodes.CheckViolation,
            constraintName: "features_deferred_check");

    private static PostgresException PostgresFailure(
        string messageText,
        string severity,
        string sqlState,
        string? constraintName = null) =>
        new(
            messageText: messageText,
            severity: severity,
            invariantSeverity: severity,
            sqlState: sqlState,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: "public",
            tableName: "features",
            columnName: null,
            dataTypeName: null,
            constraintName: constraintName,
            file: null,
            line: null,
            routine: null);

    private sealed class StubDbTransaction(Func<CancellationToken, Task> commit) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit() => throw new NotSupportedException();

        public override Task CommitAsync(CancellationToken cancellationToken = default) =>
            commit(cancellationToken);

        public override void Rollback() => throw new NotSupportedException();
    }
}
