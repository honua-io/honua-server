// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

public interface IGroundingAuthorizationFilter
{
    // Implementations must keep the input order so the filtered list stays sorted.
    Task<IReadOnlyList<GroundingCandidate>> FilterAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<GroundingCandidate> candidates,
        CancellationToken cancellationToken = default);
}
