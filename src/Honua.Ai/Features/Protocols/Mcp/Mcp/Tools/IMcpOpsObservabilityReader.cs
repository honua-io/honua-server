// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal interface IMcpOpsObservabilityReader
{
    Task<JsonElement> GetOpsHealthAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<JsonElement> GetOpsFindingsAsync(
        ClaimsPrincipal principal,
        McpOpsFindingsArgument argument,
        CancellationToken cancellationToken);

    Task<JsonElement> ListAlertEventsAsync(
        ClaimsPrincipal principal,
        McpAlertEventsArgument argument,
        CancellationToken cancellationToken);

    Task<JsonElement> ListOperateEventsAsync(
        ClaimsPrincipal principal,
        McpOperateEventsArgument argument,
        CancellationToken cancellationToken);
}
