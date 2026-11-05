// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.AiBuilder.Planning;

internal interface IPlanAnalysisService
{
    string Engine { get; }

    Task<McpPlanAnalysisOutput> PlanAsync(
        string intent,
        JsonElement? context,
        CancellationToken cancellationToken);
}
