// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Geoprocessing;

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// Contract-first stub for the <c>honua_clarify_intent</c> tool. Returns
/// structured <c>not_implemented</c> data until the clarifier service ships.
/// </summary>
internal sealed class ClarifyIntentTool : NotImplementedToolBase
{
    public const string ToolName = "honua_clarify_intent";

    public ClarifyIntentTool(IGeoprocessingJobService jobService, ILogger<ClarifyIntentTool> logger)
        : base(jobService, logger)
    {
    }

    public override string Name => ToolName;

    public override string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    protected override OperatorResourceType AuthorizedResource => OperatorResourceType.Process;

    protected override OperatorOperation AuthorizedOperation => OperatorOperation.Read;

    protected override string Description =>
        "Request operator clarification when an intent cannot be grounded unambiguously. Contract stub pending clarifier service.";

    protected override string BlockedBy => "honua.clarifier.service";

    protected override string Contract =>
        "Accepts { intent: string, ambiguities: object[] } and returns a clarification question set for the operator.";

    protected override IReadOnlyList<string> NextSteps { get; } = new[]
    {
        "Await clarifier service rollout (tracked by the intent-compiler epic)."
    };
}
