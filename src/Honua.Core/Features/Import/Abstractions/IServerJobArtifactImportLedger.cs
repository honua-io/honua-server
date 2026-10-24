// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>One committed import of an authorized job artifact.</summary>
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

/// <summary>Identity of one replay. The tenant binding is part of the key.</summary>
internal sealed record ServerJobArtifactImportScope(
    string TenantId,
    string ActorId,
    string IntentId,
    string Fingerprint,
    string Schema,
    string Table);

/// <summary>Values known before the import transaction commits the rows.</summary>
internal sealed record ServerJobArtifactImportReceiptPrototype(
    int SourceSrid,
    int TargetSrid,
    string ContentSha256,
    string CrsState);

/// <summary>
/// Remembers one committed command in the shared catalog so a restart cannot append a second copy.
/// </summary>
internal interface IServerJobArtifactImportLedger
{
    /// <summary>Returns the original receipt when the scope fingerprint still matches.</summary>
    Task<ServerJobArtifactImportReceipt> GetOrImportAsync(
        ServerJobArtifactImportScope scope,
        Func<IServerJobArtifactImportAttempt, CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
        CancellationToken cancellationToken);
}

/// <summary>Binds receipt completion to the transaction that commits the imported rows.</summary>
internal interface IServerJobArtifactImportAttempt
{
    /// <summary>Invoked by the import pipeline before it commits.</summary>
    ImportCommitParticipant Participant { get; }

    /// <summary>Records the producer facts the commit participant must persist.</summary>
    void Prepare(ServerJobArtifactImportReceiptPrototype prototype);
}

/// <summary>The intent is already committed for a different command.</summary>
internal sealed class ServerJobArtifactImportConflictException(string intentId)
    : Exception("Idempotency key is already associated with a different request.")
{
    internal string IntentId { get; } = intentId;
}

/// <summary>Another replica holds the scope lock.</summary>
internal sealed class ServerJobArtifactImportInProgressException()
    : Exception("This job artifact import is already in progress.");

/// <summary>The import returned without a receipt committed in the same transaction as the rows.</summary>
internal sealed class ServerJobArtifactImportIncompleteException()
    : Exception("The job artifact import did not commit its receipt with the rows.");
