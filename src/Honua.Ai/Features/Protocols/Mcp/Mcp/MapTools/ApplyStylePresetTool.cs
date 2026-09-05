// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// Applies a catalog preset through the canonical operation runtime. REST admin
/// authorization and operator approval precede submission; the operation policy
/// and durable audit boundary own actuation and approval replay.
/// </summary>
internal sealed class ApplyStylePresetTool : IMcpTool
{
    /// <summary>The advertised <c>tools/list</c> name of this tool.</summary>
    public const string ToolName = "honua_apply_style_preset";

    private const string ToolDescription =
        "Apply a named style preset (a reusable catalog styleId) as a published layer's primary/default style, addressed by serviceId/layerId. "
        + "The preset must already exist in the style catalog — discover the valid presets with honua_get_style (omit styleId to list them); an unknown preset is rejected and the valid presets are named. "
        + "The applied style persists through the canonical OGC API - Styles pipeline (styleId-keyed catalog + Metadata v2 graph), so a subsequent honua_render_map for the layer resolves the new style. "
        + "Requires admin write authorization and passes through operator approval and operation policy; approval-required calls do not change the layer. "
        + "It authors presentation metadata only and never edits feature records. Re-applying the same preset is a no-op (idempotent). "
        + "Styled-map arc: query/analyze -> honua_publish_result (analysis result -> serviceId/layerId) -> honua_apply_style_preset -> honua_render_map.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ApplyStylePresetTool> _logger;

    public ApplyStylePresetTool(IGeoprocessingJobService jobService, ILogger<ApplyStylePresetTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Apply style preset",
        Description = ToolDescription,
        InputSchema = MapToolSchemas.ApplyStylePresetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.ApplyStylePresetOutputSchema,
        // Write tool: it re-points the layer's primary style binding (authoring
        // presentation metadata) rather than destroying data, and re-applying the
        // same preset yields the same binding — non-destructive and idempotent.
        Annotations = McpToolAnnotationSets.Write("Apply style preset", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ApplyStylePreset");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.PublishedService, OperatorOperation.Publish, cancellationToken)
            .ConfigureAwait(false);
        var authorization = await OperationAdminAuthorization.EvaluateAsync(
            httpContext, principal, OperationSideEffectClass.MutatesMetadata, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAuthorized)
        {
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        }

        var gate = httpContext.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var approval = gate.CheckApproval(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Publish,
        });
        if (approval.IsRequired)
        {
            throw new GeoprocessingApprovalRequiredException(
                approval.PolicyRef ?? "operator.publish");
        }

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpApplyStylePresetArgument);

        if (string.IsNullOrWhiteSpace(argument.StyleId))
        {
            throw new GeoprocessingValidationException("'styleId' is required and must name a style preset.");
        }

        var styleCatalog = httpContext.RequestServices.GetService<IStyleCatalog>()
            ?? throw new GeoprocessingStoreUnavailableException("The style catalog is not available on this server.");
        var graphProvider = httpContext.RequestServices.GetService<IMetadataV2GraphProvider>()
            ?? throw new GeoprocessingStoreUnavailableException("The metadata catalog is not available on this server.");

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layer = MapToolLayerResolver.Resolve(snapshot, argument.ServiceId, argument.LayerId);

        var styleId = argument.StyleId!.Trim();
        var preset = await styleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (preset is null)
        {
            var available = await styleCatalog.ListStylesAsync(cancellationToken).ConfigureAwait(false);
            var presetIds = available.Count == 0
                ? "(none available)"
                : string.Join(", ", available.Select(s => s.StyleId));
            throw new GeoprocessingValidationException(
                $"Unknown style preset '{styleId}'. Valid presets: {presetIds}. "
                + "List presets with honua_get_style (omit styleId).");
        }

        var invoker = httpContext.RequestServices.GetRequiredService<IOperationInvoker>();
        var operation = await invoker.SubmitAsync(new OperationRequest
        {
            OperationId = "style.apply-preset",
            Parameters = new Dictionary<string, string?>
            {
                ["serviceId"] = layer.Service.Metadata.Id,
                ["layerId"] = argument.LayerId!.Value.ToString(CultureInfo.InvariantCulture),
                ["styleId"] = styleId,
            },
        }, new OperationPolicyContext
        {
            PrincipalId = McpAuthorizationHelper.ResolveActorId(principal),
            AuthorizationOutcome = authorization.AuthorizationOutcome,
            TenantId = httpContext.RequestServices.GetService<ITenantContext>()?.TenantId,
            SchemaName = httpContext.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
            Roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
            ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
            RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
                .OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
        }, cancellationToken).ConfigureAwait(false);
        if (operation.Status == OperationHandleStatus.RequiresApproval)
        {
            throw new GeoprocessingApprovalRequiredException(
                operation.ApprovalLane ?? operation.OperationId, operation.Reason, operation.ProposalId);
        }
        if (operation.Status == OperationHandleStatus.Denied)
        {
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        }
        if (operation.Status != OperationHandleStatus.Completed)
        {
            throw new GeoprocessingPreconditionFailedException(
                operation.Reason ?? "The style preset operation did not complete.");
        }

        var output = new McpApplyStylePresetOutput
        {
            ServiceId = layer.Service.Metadata.Id,
            LayerId = argument.LayerId!.Value,
            StyleId = preset.StyleId,
            Title = preset.Title,
            StyleVersion = preset.StyleVersion,
            Applied = true
        };
        return McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpApplyStylePresetOutput);
    }
}
