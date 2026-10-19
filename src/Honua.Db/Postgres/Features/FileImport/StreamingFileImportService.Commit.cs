// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private async Task<ImportExecutionCommitBinding> RequireCurrentCommitBindingAsync(ImportRequest request,
        string schema, string table, CancellationToken cancellationToken)
    {
        var authority = _commitAuthority
            ?? throw new UnauthorizedAccessException("Current accepted import commit authority is unavailable.");
        var binding = await authority.RequireCurrentAsync(request, schema, table, cancellationToken).ConfigureAwait(false);
        var scope = JobSecurityScope.Current;
        if (scope?.Submitter?.ExecutionLease is null || scope.JobId != binding.OperationId
            || scope.IntentFingerprint != binding.IntentFingerprint)
            throw new UnauthorizedAccessException("The commit authority does not match the actual accepted import scope.");
        cancellationToken.ThrowIfCancellationRequested();
        return binding;
    }

    private async Task PublishNewAcceptedImportAsync(NpgsqlConnection connection, ImportRequest request,
        string schema, string table, string stagingTable, ImportExecutionCommitBinding originalBinding,
        ImportResult originalResult, CancellationToken cancellationToken)
    {
        // Encode the complete result before any destination mutation. Oversize diagnostics
        // cannot leave committed data without its recoverable original result.
        var payload = ImportCommittedResultCodec.Encode(originalResult);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresImportCommitReceipt.LockDecisionAsync(connection, transaction, _schemaConfiguration.MetadataSchema,
            originalBinding.OperationId, originalBinding.IntentFingerprint, cancellationToken).ConfigureAwait(false);
        var current = await RequireCurrentCommitBindingAsync(request, schema, table, cancellationToken).ConfigureAwait(false);
        if (current != originalBinding || !current.RequireAbsentTarget)
            throw new UnauthorizedAccessException("The accepted import destination precondition changed before commit.");
        await ImportTableOwnership.AssertExistingOwnedAsync(connection, schema, stagingTable, cancellationToken).ConfigureAwait(false);

        // RENAME itself enforces absence under PostgreSQL's catalog locks, even if a
        // non-Honua writer creates the destination after the policy preflight. Never
        // call the replace helper, which may drop an existing importer-owned table.
        await using (var publish = new NpgsqlCommand(
            $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(stagingTable)} RENAME TO {QuoteIdentifier(table)}; " +
            $"ALTER INDEX IF EXISTS {QuoteIdentifier(schema)}.{QuoteIdentifier("idx_" + stagingTable + "_geometry")} RENAME TO {QuoteIdentifier("idx_" + table + "_geometry")}; " +
            $"ALTER INDEX IF EXISTS {QuoteIdentifier(schema)}.{QuoteIdentifier("idx_" + stagingTable + "_properties")} RENAME TO {QuoteIdentifier("idx_" + table + "_properties")};",
            connection, transaction))
        {
            await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await PostgresImportCommitReceipt.WriteAsync(connection, transaction, _schemaConfiguration.MetadataSchema,
            current.OperationId, current.IntentFingerprint, payload, cancellationToken).ConfigureAwait(false);

        // Policy work and database work both await. Refresh the same canonical job,
        // source and destination authority immediately before making the transaction durable.
        if (await RequireCurrentCommitBindingAsync(request, schema, table, cancellationToken).ConfigureAwait(false) != originalBinding)
            throw new UnauthorizedAccessException("The accepted import binding changed during commit.");
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }
}
