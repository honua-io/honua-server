// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// MCP tool that records publication intent on a Studio draft
/// (honua-server#3002, REQ-003/REQ-009). This is the ONLY publish-adjacent
/// tool on the agent surface, and it deliberately does not publish anything:
/// it sets <see cref="StudioPackageEnvelope.PublicationIntent"/> on the draft
/// through the ordinary generation-checked
/// <see cref="IStudioPackageLifecycleService.UpdateDraftAsync"/> path — the
/// same envelope field the Studio UI reads to render a pending-review
/// affordance — and appends a provenance entry recording who proposed it and
/// when.
/// <para>
/// It never calls <c>CreatePublicationRequestAsync</c>,
/// <c>SaveDraftAsVersionAsync</c>, or <c>RollbackAsync</c>: those are the
/// version-level lifecycle operations that actually move a
/// current/published pointer (see
/// <c>InMemoryStudioPackageStore.CreatePublicationRequestAsync</c>'s
/// <c>Accepted</c> branch), and none of them are reachable from this or any
/// other tool in this file. Publish/share/embed execution stays a
/// human-confirmed action taken outside the agent tool surface (the Studio
/// REST admin API / console UI), per REQ-009.
/// </para>
/// </summary>
internal sealed class ProposeStudioPublicationTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_propose_publication";

    private readonly ILogger<ProposeStudioPublicationTool> _typedLogger;

    public ProposeStudioPublicationTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<ProposeStudioPublicationTool> logger)
        : base(lifecycleService, jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Propose Studio publication",
        Description =
            "Record publication intent (route, visibility, embed, service, schedule, job) on a Studio draft for human review. "
            + "This tool ONLY records intent on the draft — it never publishes, shares, embeds, or moves a current/published "
            + "pointer. Publish/share/embed execution is a human-confirmed action taken outside the agent tool surface.",
        InputSchema = StudioMcpSchemas.ProposePublicationArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioProposePublicationOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Propose Studio publication", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioProposePublication");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioProposePublicationArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = AddStudioLayerTool.RequireGeneration(argument.Generation);

        var draft = await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var actorId = ActorIdFor(principal);

        var intent = new StudioPublicationIntent
        {
            Route = argument.Route,
            Visibility = argument.Visibility,
            Embed = argument.Embed,
            Service = argument.Service,
            Schedule = argument.Schedule,
            Job = argument.Job,
        };

        var provenance = draft.Envelope.Provenance
            .Append(new StudioProvenanceRef
            {
                Kind = "agent-proposal",
                Ref = argument.Note ?? "publication proposed by agent",
                Rel = "proposes-publication",
                ActorId = actorId,
                Timestamp = DateTimeOffset.UtcNow,
            })
            .ToArray();

        var envelope = draft.Envelope with { PublicationIntent = intent, Provenance = provenance };

        var updated = await ApplyUpdateAsync(
            draftId,
            EnvelopeOnlyUpdate(draft, envelope, generation, actorId),
            cancellationToken).ConfigureAwait(false);

        Audit(principal, ToolName, draftId, generationBefore: generation, generationAfter: updated.Generation);

        var output = new McpStudioProposePublicationOutput
        {
            Draft = updated,
            Recorded = true,
            HumanConfirmationRequired = true,
            Message =
                "Publication intent recorded on the draft for human review. "
                + "No publish, share, or embed action was executed; a human must confirm publication through the Studio UI/REST admin surface.",
        };

        return McpToolHelpers.SuccessResult(output, StudioMcpJsonContext.Default.McpStudioProposePublicationOutput);
    }
}
