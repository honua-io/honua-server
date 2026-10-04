// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Validation;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// Collaborators used only by gRPC <c>ApplyEdits</c>: the at-most-once replay store and the
/// shared mutation validator that holds adds and updates to the layer schema.
/// </summary>
internal sealed class GrpcApplyEditsDependencies
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcApplyEditsDependencies"/> class.
    /// </summary>
    /// <param name="idempotencyStore">Replay store for keyed <c>ApplyEdits</c> retries.</param>
    /// <param name="mutationValidator">Shared attribute and geometry validator.</param>
    public GrpcApplyEditsDependencies(
        GrpcApplyEditsIdempotencyStore idempotencyStore,
        FeatureMutationValidator mutationValidator)
    {
        IdempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
    }

    /// <summary>Gets the replay store for keyed <c>ApplyEdits</c> retries.</summary>
    public GrpcApplyEditsIdempotencyStore IdempotencyStore { get; }

    /// <summary>Gets the shared attribute and geometry validator.</summary>
    public FeatureMutationValidator MutationValidator { get; }
}
