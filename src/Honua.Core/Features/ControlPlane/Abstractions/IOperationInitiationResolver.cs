// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>Resolves operation provenance from the authenticated server-owned request context.</summary>
public interface IOperationInitiationResolver
{
    /// <summary>Returns the verified initiator and channel, or unknown when no request can be verified.</summary>
    Task<(OperationInitiationKind Kind, OperationInitiationChannel Channel)> ResolveAsync(
        CancellationToken cancellationToken);
}
