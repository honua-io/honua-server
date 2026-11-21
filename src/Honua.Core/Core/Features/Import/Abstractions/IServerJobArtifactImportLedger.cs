// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

internal sealed record ServerJobArtifactImportReceipt(
    string IntentId,
    string Schema,
    string Table,
    string PhysicalTable,
    int FeatureCount,
    int SourceSrid,
    int TargetSrid,
    string ContentSha256,
    string CrsState);

internal sealed record ServerJobArtifactImportScope(
    string TenantId,
    string ActorId,
    string IntentId,
    string Fingerprint,
    string Schema,
    string Table);

internal sealed record ServerJobArtifactImportReceiptPrototype(
    int SourceSrid,
    int TargetSrid,
    string ContentSha256,
    string CrsState);

internal sealed record ServerJobArtifactImportDiscardScope(
    string TenantId,
    string ActorId,
    string IntentId,
    string ExpectedTable);

internal sealed record ServerJobArtifactImportDiscardReceipt(
    string IntentId,
    string Schema,
    string Table,
    string PhysicalTable,
    bool AlreadyDiscarded);

// The shared catalog remembers one committed command so a restart cannot append a second copy.
internal interface IServerJobArtifactImportLedger
{
    Task<ServerJobArtifactImportReceipt> GetOrImportAsync(
        ServerJobArtifactImportScope scope,
        Func<IServerJobArtifactImportAttempt, CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
        CancellationToken cancellationToken);

    Task<ServerJobArtifactImportDiscardReceipt> DiscardAsync(
        ServerJobArtifactImportDiscardScope scope,
        CancellationToken cancellationToken);
}

internal interface IServerJobArtifactImportAttempt
{
    ImportCommitParticipant Participant { get; }

    void Prepare(ServerJobArtifactImportReceiptPrototype prototype);
}

internal sealed class ServerJobArtifactImportConflictException(string intentId)
    : Exception("Idempotency key is already associated with a different request.")
{
    internal string IntentId { get; } = intentId;
}

internal sealed class ServerJobArtifactImportInProgressException()
    : Exception("This job artifact import is already in progress.");

internal sealed class ServerJobArtifactImportIncompleteException()
    : Exception("The job artifact import did not commit its receipt with the rows.");

internal sealed class ServerJobArtifactImportNotFoundException(string intentId)
    : Exception($"No committed staged import receipt was found for intent '{intentId}'.")
{
    internal string IntentId { get; } = intentId;
}

internal sealed class ServerJobArtifactImportDiscardedException(string intentId)
    : Exception($"Staged import intent '{intentId}' was already discarded; use a new intentId to import again.")
{
    internal string IntentId { get; } = intentId;
}

internal sealed class ServerJobArtifactImportReferencedException(string schema, string table)
    : Exception($"Staged table '{schema}.{table}' is referenced by a published layer or storage binding and cannot be discarded.")
{
    internal string Schema { get; } = schema;

    internal string Table { get; } = table;
}

internal sealed class ServerJobArtifactImportNotOwnedException(string schema, string table)
    : Exception($"Table '{schema}.{table}' is not an import-owned staged table and cannot be discarded.")
{
    internal string Schema { get; } = schema;

    internal string Table { get; } = table;
}
