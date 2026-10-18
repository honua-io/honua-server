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
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/operations/{operationInstanceId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project/identity"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project/bind"),
        new("DELETE", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project/bindings/{bindingId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}"),
        new("PUT", "/api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}/sources/{**path}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/cancel"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/commands/{commandId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/artifacts/{**path}"),
    ];
}
