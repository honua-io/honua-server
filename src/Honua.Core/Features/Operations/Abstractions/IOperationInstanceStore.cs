// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Durable source of truth for canonical operation-instance envelopes.
/// </summary>
public interface IOperationInstanceStore
{
    /// <summary>Creates an operation instance before authorization or policy evaluation.</summary>
    Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default);

    /// <summary>Persists the latest projection of an existing operation instance.</summary>
    Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default);

    /// <summary>Reads an operation instance by its canonical invocation identity.</summary>
    Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default);
}
