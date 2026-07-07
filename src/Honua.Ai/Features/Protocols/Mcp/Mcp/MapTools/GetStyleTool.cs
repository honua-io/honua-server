// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that inspects styles served through the canonical OGC API - Styles
/// surface (ADR-0048). It is a thin, read-only adapter over the styleId-keyed
/// <see cref="IStyleCatalog"/> and the neutral <see cref="IOgcStyleProjection"/>
/// encoding projection — the same seams the <c>/ogc/styles</c> endpoints drive —
/// so no style storage, conversion, or catalog logic is reimplemented here.
///
/// <para>
/// Three modes, none of which mutate server state:
/// <list type="bullet">
///   <item><description>With <c>styleId</c>: resolves that single StyleRef.</description></item>
///   <item><description>With <c>serviceId</c>+<c>layerId</c>: resolves the primary/default
///     StyleRef bound to that published layer.</description></item>
///   <item><description>With none: lists the available styles (a discovery catalog).</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class GetStyleTool : IMcpTool
{
    /// <summary>The advertised <c>tools/list</c> name of this tool.</summary>
    public const string ToolName = "honua_get_style";

    private const string ToolDescription =
        "Inspect a layer's or a catalog style's stylesheet/renderer (read-only) via the OGC API - Styles surface. "
        + "Resolve a single style by 'styleId', or resolve a published layer's primary/default style by 'serviceId'+'layerId'; "
        + "omit all three to list the available styles (a discovery catalog of styleId/title). "
        + "A resolved style advertises its encodings — MapLibre style JSON ('mapbox-style', the canonical form), Esri 'esri-drawing-info' (drawingInfo renderer), and SLD 'sld-1.0.0'/'sld-1.1.0' — each by reference; "
        + "set includeStylesheet=true (with 'encoding', default 'mapbox-style') to inline the selected stylesheet body. "
        + "Use the returned styleId as a preset for honua_apply_style_preset to make a layer render in that style. "
        + "Part of the styled-map arc: query/analyze -> honua_publish_result -> honua_apply_style_preset -> honua_render_map.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<GetStyleTool> _logger;

    public GetStyleTool(IGeoprocessingJobService jobService, ILogger<GetStyleTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Get style",
        Description = ToolDescription,
        InputSchema = MapToolSchemas.GetStyleArgumentSchema,
        OutputSchema = McpToolOutputSchemas.GetStyleOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Get style")
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetStyle");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        // Reading style/presentation metadata for a published service is gated on
        // the same operator-read grant the honua://published-services resource
        // enforces, so a query-capable principal can inspect styles.
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.PublishedService, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpGetStyleArgument);

        var styleCatalog = httpContext.RequestServices.GetService<IStyleCatalog>()
            ?? throw new GeoprocessingStoreUnavailableException("The style catalog is not available on this server.");

        // List mode: no styleId and no layer address -> discovery catalog.
        var hasStyleId = !string.IsNullOrWhiteSpace(argument.StyleId);
        var hasLayer = !string.IsNullOrWhiteSpace(argument.ServiceId) || argument.LayerId is not null;
        if (!hasStyleId && !hasLayer)
        {
            var styles = await styleCatalog.ListStylesAsync(cancellationToken).ConfigureAwait(false);
            return McpToolHelpers.SuccessResult(
                BuildListOutput(styles),
                MapToolJsonContext.Default.McpGetStyleOutput,
                SummarizeStyleOutput);
        }

        var styleId = hasStyleId
            ? argument.StyleId!.Trim()
            : await ResolveLayerStyleIdAsync(httpContext, styleCatalog, argument, cancellationToken).ConfigureAwait(false);

        var record = await styleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Style '{styleId}' was not found in the style catalog.");

        var encoding = ParseEncoding(argument.Encoding);
        var includeStylesheet = argument.IncludeStylesheet ?? false;
        var output = await BuildStyleOutputAsync(httpContext, record, encoding, includeStylesheet, cancellationToken)
            .ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(
            output,
            MapToolJsonContext.Default.McpGetStyleOutput,
            SummarizeStyleOutput);
    }

    /// <summary>
    /// One-line, information-bearing text summary emitted when the serialized
    /// style payload is large (a big discovery catalog, or an inlined stylesheet
    /// body): the essential identifiers and next-step hints live in text while
    /// the full payload rides in <c>structuredContent</c> (MCP A1 token policy).
    /// </summary>
    private static string SummarizeStyleOutput(McpGetStyleOutput output)
    {
        if (output.Styles is { } styles)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Listed {0} style(s) in the catalog. Full styleId/title/uri entries in structuredContent.styles; "
                + "pass a styleId to honua_apply_style_preset to style a layer.",
                styles.Count);
        }

        var inlined = output.Encodings?.FirstOrDefault(e => e.InlineBody is not null);
        var encodingNote = inlined is null
            ? "encodings advertised by reference"
            : string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "'{0}' stylesheet inlined ({1:N0} chars)",
                inlined.Encoding,
                inlined.InlineBody!.Length);

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Resolved style '{0}' ({1}, v{2}; {3}). Full projection in structuredContent; "
            + "apply it to a layer with honua_apply_style_preset.",
            output.StyleId,
            output.Title ?? "untitled",
            output.StyleVersion ?? 0,
            encodingNote);
    }

    private static async Task<string> ResolveLayerStyleIdAsync(
        HttpContext httpContext,
        IStyleCatalog styleCatalog,
        McpGetStyleArgument argument,
        CancellationToken cancellationToken)
    {
        var graphProvider = httpContext.RequestServices.GetService<IMetadataV2GraphProvider>()
            ?? throw new GeoprocessingStoreUnavailableException("The metadata catalog is not available on this server.");
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layer = MapToolLayerResolver.Resolve(snapshot, argument.ServiceId, argument.LayerId);

        var styles = await styleCatalog.GetStylesForLayerAsync(layer.StorageLayerId, cancellationToken).ConfigureAwait(false);
        if (styles.Count == 0)
        {
            throw new GeoprocessingNotFoundException(
                $"Layer {argument.LayerId} on service '{argument.ServiceId}' has no style bound. "
                + "Apply one with honua_apply_style_preset.");
        }

        // Ordinal 0 (first) is the primary/default style by convention.
        return styles[0].StyleId;
    }

    private static async Task<McpGetStyleOutput> BuildStyleOutputAsync(
        HttpContext httpContext,
        StyleCatalogRecord record,
        OgcStyleEncoding selected,
        bool includeStylesheet,
        CancellationToken cancellationToken)
    {
        var projection = httpContext.RequestServices.GetService<IOgcStyleProjection>();

        var encodings = new List<McpStyleEncodingRef>(SupportedEncodings.Length);
        foreach (var (encoding, wire, mediaType) in SupportedEncodings)
        {
            string? inlineBody = null;
            if (includeStylesheet && encoding == selected)
            {
                if (projection is not null)
                {
                    var sheet = await projection.GetStylesheetAsync(record.StyleId, encoding, cancellationToken)
                        .ConfigureAwait(false);
                    inlineBody = sheet?.Content;
                }
                else if (encoding == OgcStyleEncoding.MapboxStyle)
                {
                    // Canonical MapLibre style is stored on the record; other encodings
                    // are derived by the projection, which is not composed here.
                    inlineBody = record.MapLibreStyleJson;
                }
            }

            encodings.Add(new McpStyleEncodingRef
            {
                Encoding = wire,
                MediaType = mediaType,
                InlineBody = inlineBody,
                StorageRef = inlineBody is null ? $"honua://styles/{record.StyleId}" : null
            });
        }

        return new McpGetStyleOutput
        {
            StyleId = record.StyleId,
            Title = record.Title,
            Description = record.Description,
            StyleVersion = record.StyleVersion,
            Encodings = encodings
        };
    }

    private static McpGetStyleOutput BuildListOutput(IReadOnlyList<StyleCatalogRecord> styles)
    {
        var summaries = new McpStyleSummary[styles.Count];
        for (var i = 0; i < styles.Count; i++)
        {
            var style = styles[i];
            summaries[i] = new McpStyleSummary
            {
                StyleId = style.StyleId,
                Title = style.Title,
                Uri = $"honua://styles/{style.StyleId}"
            };
        }

        return new McpGetStyleOutput { Styles = summaries };
    }

    private static OgcStyleEncoding ParseEncoding(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return OgcStyleEncoding.MapboxStyle;
        }

        return encoding.Trim().ToLowerInvariant() switch
        {
            "mapbox-style" => OgcStyleEncoding.MapboxStyle,
            "sld-1.0.0" => OgcStyleEncoding.Sld10,
            "sld-1.1.0" => OgcStyleEncoding.Sld11,
            "esri-drawing-info" => OgcStyleEncoding.EsriDrawingInfo,
            _ => throw new GeoprocessingValidationException(
                $"Unsupported style encoding '{encoding}'. Supported encodings: "
                + "mapbox-style, sld-1.0.0, sld-1.1.0, esri-drawing-info.")
        };
    }

    // The encodings the OGC API - Styles surface can produce for a catalog style,
    // in advertise order: canonical MapLibre first, then the derived encodings.
    private static readonly (OgcStyleEncoding Encoding, string Wire, string MediaType)[] SupportedEncodings =
    [
        (OgcStyleEncoding.MapboxStyle, "mapbox-style", "application/vnd.mapbox.style+json"),
        (OgcStyleEncoding.EsriDrawingInfo, "esri-drawing-info", "application/vnd.esri.drawinginfo+json"),
        (OgcStyleEncoding.Sld10, "sld-1.0.0", "application/vnd.ogc.sld+xml;version=1.0"),
        (OgcStyleEncoding.Sld11, "sld-1.1.0", "application/vnd.ogc.sld+xml;version=1.1"),
    ];
}
