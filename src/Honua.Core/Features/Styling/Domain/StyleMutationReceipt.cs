// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>Trusted canonical execution identity, never accepted from a model or HTTP style document.</summary>
public sealed record StyleMutationIdentity(string OperationId, string ProposalId, string RequestHash,
    string ActorId, string TenantId, string SchemaName, string TargetId, string StyleId,
    int ExpectedVersion, string ExpectedContentHash, string ExpectedImpactHash, string CandidateHash, string ExpectedStoredContentHash);

/// <summary>Durable proof committed atomically with canonical bytes; contains no credentials or style documents.</summary>
public sealed record StyleMutationReceipt(StyleMutationIdentity Identity, int ResultVersion,
    string ResultContentHash, DateTimeOffset CommittedAt);

/// <summary>A new committed edit or a reconciled immutable receipt.</summary>
public sealed record StyleMutationOutcome(StyleMutationReceipt Receipt, StyleEditStoreSnapshot? UpdatedSnapshot);
