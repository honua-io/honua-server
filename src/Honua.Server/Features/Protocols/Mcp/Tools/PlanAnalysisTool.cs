// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Geoprocessing;

namespace Honua.Server.Features.Protocols.Mcp.Tools;

/// <summary>
/// Contract-first stub for the <c>honua_plan_analysis</c> tool. Returns
/// structured <c>not_implemented</c> data until the planner service ships.
/// </summary>
internal sealed class PlanAnalysisTool : NotImplementedToolBase
{
    public const string ToolName = "honua_plan_analysis";

    public PlanAnalysisTool(IGeoprocessingJobService jobService, ILogger<PlanAnalysisTool> logger)
        : base(jobService, logger)
    {
    }

    public override string Name => ToolName;

    public override string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    protected override OperatorResourceType AuthorizedResource => OperatorResourceType.Process;

    protected override OperatorOperation AuthorizedOperation => OperatorOperation.Read;

    protected override string Description =>
        "Generate an analysis plan from a natural-language intent. Contract stub pending planner service.";

    protected override string BlockedBy => "honua.planner.service";

    protected override string Contract =>
        "Accepts { intent: string, context?: object } and returns an AnalysisPlan suitable for validate_plan/execute_plan.";

    protected override IReadOnlyList<string> NextSteps { get; } = new[]
    {
        "Await planner service rollout (tracked by the intent-compiler epic).",
        "Use honua_validate_plan with a precomputed plan until the stub is replaced."
    };
}
