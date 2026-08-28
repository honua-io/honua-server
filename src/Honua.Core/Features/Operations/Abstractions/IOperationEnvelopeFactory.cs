// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Creates and durably accepts canonical operation-instance envelopes before policy,
/// proposal persistence, or actuation.
/// </summary>
public interface IOperationEnvelopeFactory
{
    /// <summary>
    /// Creates the invocation identity, persists its accepted envelope, writes the joined
    /// acceptance audit receipt, and returns the durable projection.
    /// </summary>
    Task<OperationHandle> CreateAcceptedAsync(
        string operationId,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);
}
