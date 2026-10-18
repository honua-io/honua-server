// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    private static IReadOnlyList<EndpointDefinition> AgentWorkspaceEndpoints =>
    [
        new("GET", "/api/v1/agent/target"),
        new("POST", "/api/v1/agent/devops/authorize"),
        new("POST", "/api/v1/agent/workspaces"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}"),
        new("PUT", "/api/v1/agent/workspaces/{workspaceId}/context"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/turns"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/turns/{turnId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/conversation"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/conversation"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/drafts"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}"),
        new("PATCH", "/api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}/submit"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/commands/{commandId}"),
    ];
}
