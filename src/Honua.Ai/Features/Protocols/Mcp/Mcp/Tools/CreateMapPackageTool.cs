// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

// The factory is a constructor dependency so a missing registration fails at startup instead of advertising a tool that always returns unavailable.
internal sealed class CreateMapPackageTool : IMcpTool
{
    public const string ToolName = "honua_create_map_package";

    private readonly IGeoprocessingJobService _jobService;
    private readonly IMapPackageDraftFactory _drafts;
    private readonly IPackageDraftStore _draftStore;
    private readonly ILogger<CreateMapPackageTool> _logger;

    public CreateMapPackageTool(
        IGeoprocessingJobService jobService,
        IMapPackageDraftFactory drafts,
        IPackageDraftStore draftStore,
        ILogger<CreateMapPackageTool> logger)
    {
        _jobService = jobService;
        _drafts = drafts;
        _draftStore = draftStore;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Create map package",
        Description = "Create a MapPackage draft deterministically from structured composition input "
            + "(templateId, sourceBindings, styleId, themeId, initialView). Performs no model inference and takes no "
            + "natural-language input; returns a draft package with a stable map_… identifier and its "
            + "honua://map-packages/{id} resource URI, plus any deferred-resolution warnings.",
        InputSchema = McpToolSchemas.CreateMapPackageArgumentSchema,
        OutputSchema = McpToolOutputSchemas.CreateMapPackageOutputSchema,
        // Write tool: it authors a new MapPackage draft. Not destructive (it
        // creates rather than destroys state) and not idempotent — the draft is
        // a pure function of the input except for its freshly minted identity,
        // so a replay yields an equivalent package under a new map_… id.
        Annotations = McpToolAnnotationSets.Write("Create map package", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("CreateMapPackage");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Package, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var ownerId = PackageDraftCaller.RequireOwnerId(principal);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpCreateMapPackageArgument);

        var result = _drafts.CreateDraft(new MapPackageDraftRequest
        {
            TemplateId = argument.TemplateId,
            StyleId = argument.StyleId,
            ThemeId = argument.ThemeId,
            SourceBindings = MapSourceBindings(argument.SourceBindings),
            InitialView = MapInitialView(argument.InitialView)
        });

        if (result.Package is null)
        {
            throw new GeoprocessingValidationException(McpPackageDraftProjection.DescribeErrors(
                "Map-package draft input is invalid", result.Errors));
        }

        // Persist before returning: ADR-0076 promises a package "addressable at
        // its honua://map-packages/{id} URI", and MapPackageResource can only
        // honour that for a deployment-less draft if the draft was recorded
        // (honua-server#3262).
        await _draftStore.SaveMapDraftAsync(result.Package, ownerId, cancellationToken).ConfigureAwait(false);

        var output = new McpPackageDraftOutput
        {
            PackageId = result.Package.MapPackageId,
            ResourceUri = McpResourceUris.MapPackageUri(result.Package.MapPackageId),
            Package = JsonSerializer.SerializeToElement(result.Package, PackagingJsonContext.Default.MapPackage),
            Warnings = McpPackageDraftProjection.MapFindings(result.Warnings)
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpPackageDraftOutput);
    }

    private static List<SourceBindingInput> MapSourceBindings(
        IReadOnlyList<McpSourceBindingArgument?>? bindings)
    {
        if (bindings is not { Count: > 0 })
        {
            return [];
        }

        var mapped = new List<SourceBindingInput>(bindings.Count);
        foreach (var binding in bindings)
        {
            mapped.Add(new SourceBindingInput
            {
                // The vendored standard fixture spells the identifier bindingId;
                // the canonical SourceBinding spells it sourceId. Accept both.
                SourceId = binding?.SourceId ?? binding?.BindingId,
                Protocol = binding?.Protocol,
                Url = binding?.Url,
                ServiceId = binding?.ServiceId,
                LayerId = binding?.LayerId,
                Filter = binding?.Filter,
                Metadata = binding?.Metadata
            });
        }

        return mapped;
    }

    private static MapInitialViewInput? MapInitialView(McpInitialViewArgument? initialView)
    {
        if (initialView is null)
        {
            return null;
        }

        return new MapInitialViewInput
        {
            Bbox = initialView.Bbox,
            Crs = NormalizeCrs(initialView.Crs)
        };
    }

    private static string? NormalizeCrs(JsonElement? crs) => crs?.ValueKind switch
    {
        JsonValueKind.String => crs.Value.GetString(),
        JsonValueKind.Number when crs.Value.TryGetInt32(out var code) => "EPSG:" + code.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.Null or JsonValueKind.Undefined or null => null,

        // Anything else is surfaced verbatim so the shared factory rejects it as
        // an unsupported CRS rather than the adapter silently coercing it.
        _ => crs.Value.GetRawText()
    };
}
