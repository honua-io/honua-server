// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Spec;

/// <summary>
/// Integration seam for the canonical AgentWorkspace authority service. Implementations
/// must load the current persisted view, verify live membership/resource grants for the
/// supplied principal, match the captured target and publish draft, and return a hash of
/// the authoritative snapshot. A client revision or a forwarded header is not a snapshot.
/// </summary>
internal interface IGaSpecContextReader
{
    Task<string> ReadAuthorizedContextHashAsync(SpecLayerIntent intent, string tenant,
        string actor, CancellationToken cancellationToken, bool requireCurrent = true);
}

// AgentWorkspace lives on the unlanded #6297 branch. Do not create an alternative
// workspace store or trust client metadata while that prerequisite is absent.
internal sealed class UnavailableGaSpecContextReader : IGaSpecContextReader
{
    public Task<string> ReadAuthorizedContextHashAsync(SpecLayerIntent intent, string tenant,
        string actor, CancellationToken cancellationToken, bool requireCurrent = true)
        => throw new GaSpecBlockedException("workspace-context-unavailable");
}
