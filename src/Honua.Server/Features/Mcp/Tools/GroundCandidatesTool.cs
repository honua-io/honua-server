// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// Contract-first stub for the <c>honua_ground_candidates</c> tool. Returns
/// structured <c>not_implemented</c> data until the grounding service ships.
/// </summary>
internal sealed class GroundCandidatesTool : NotImplementedToolBase
{
    public const string ToolName = "honua_ground_candidates";

    public GroundCandidatesTool(ILogger<GroundCandidatesTool> logger)
        : base(logger)
    {
    }

    public override string Name => ToolName;

    public override string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    protected override string Description =>
        "Ground an intent to candidate datasets, processes, and workspaces. Contract stub pending grounding service.";

    protected override string BlockedBy => "honua.grounding.service";

    protected override string Contract =>
        "Accepts { intent: string, filters?: object } and returns ranked candidates for datasets, processes, and workspaces.";

    protected override IReadOnlyList<string> NextSteps { get; } = new[]
    {
        "Await grounding service rollout (tracked by the intent-compiler epic).",
        "Use honua://catalog/processes once the catalog resource lights up."
    };
}
