// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

// Classification, ranking, and clarification must be deterministic for a given engine and catalog snapshot; a new intent id is intentionally unique.
public interface IGroundingService
{
    Task<GroundingResult> GroundAsync(
        GroundingRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}
