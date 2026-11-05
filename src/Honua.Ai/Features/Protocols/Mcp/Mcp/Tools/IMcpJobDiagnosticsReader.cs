// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal interface IMcpJobDiagnosticsReader
{
    Task<JsonElement> DiagnoseJobAsync(
        ClaimsPrincipal principal,
        McpDiagnoseJobArgument argument,
        CancellationToken cancellationToken);

    Task<JsonElement> GetJobEvidenceAsync(
        ClaimsPrincipal principal,
        McpJobEvidenceArgument argument,
        CancellationToken cancellationToken);
}
