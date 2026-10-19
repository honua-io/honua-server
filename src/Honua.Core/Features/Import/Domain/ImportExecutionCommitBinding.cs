// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>Canonical accepted-job identity and database precondition; never a transferable capability.</summary>
/// <param name="OperationId">Actual persisted job identifier.</param>
/// <param name="IntentFingerprint">Full original job intent fingerprint.</param>
/// <param name="RequireAbsentTarget">True only when sealed intent requires creation of a new destination.</param>
public sealed record ImportExecutionCommitBinding(string OperationId, string IntentFingerprint, bool RequireAbsentTarget);
