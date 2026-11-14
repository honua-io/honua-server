// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IAgentAuthorityStore
{
    Task<AgentAuthorityCeiling?> GetAsync(string? tenantId, string principalId, CancellationToken cancellationToken = default);

    Task SetAsync(AgentAuthorityCeiling ceiling, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string? tenantId, string principalId, CancellationToken cancellationToken = default);
}
