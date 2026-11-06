// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal interface IMcpPlatformOpsReader
{
    Task<JsonElement> GetPlatformReleaseStatusAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<JsonElement> GetDeployOperationsAsync(
        ClaimsPrincipal principal,
        McpDeployOperationsArgument argument,
        CancellationToken cancellationToken);

    Task<McpSupportedOperationKindsOutput> GetSupportedOperationKindsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<McpProposeOperationOutput> ProposeRollbackAsync(
        ClaimsPrincipal principal,
        McpProposeRollbackArgument argument,
        CancellationToken cancellationToken);

    Task<McpProposeOperationOutput> ProposeFindingAsync(ClaimsPrincipal principal, McpProposeFindingArgument argument, CancellationToken cancellationToken);
    Task<McpProposeOperationOutput> ProposeDeployPlanAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, CancellationToken cancellationToken);
    Task<McpProposeOperationOutput> ProposeDeployOperationAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, CancellationToken cancellationToken);
    Task<McpProposeOperationOutput> ProposeMetadataReleaseAsync(ClaimsPrincipal principal, McpMetadataReleaseMutationArgument argument, CancellationToken cancellationToken);
    Task<McpProposeOperationOutput> ProposePlatformReleaseConvergenceAsync(ClaimsPrincipal principal, McpPlatformReleaseConvergenceArgument argument, CancellationToken cancellationToken);
}
