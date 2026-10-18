// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AgentWorkspace.Domain;

/// <summary>Immutable server-owned identity of a turn authorized for the requesting agent.</summary>
/// <param name="WorkspaceId">Workspace whose explicit membership was checked.</param>
/// <param name="TurnId">Durable turn whose frozen context and current authority were checked.</param>
/// <param name="TargetId">Actual deployment target captured by the turn.</param>
/// <param name="TenantId">Server-resolved tenant owning the authorized turn.</param>
/// <param name="ReleaseId">Configured release identity permitted to request signed transcripts.</param>
public sealed record AgentWorkspaceTurnAuthorization(
    string WorkspaceId,
    string TurnId,
    string TargetId,
    string TenantId,
    string ReleaseId);
