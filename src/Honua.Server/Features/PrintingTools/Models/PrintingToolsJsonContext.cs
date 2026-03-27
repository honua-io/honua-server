// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.PrintingTools.Models;

/// <summary>
/// AOT-compatible JSON serialization context for PrintingTools models.
/// </summary>
[JsonSerializable(typeof(WebMapDefinition))]
[JsonSerializable(typeof(WebMapExtent))]
[JsonSerializable(typeof(WebMapBbox))]
[JsonSerializable(typeof(WebMapSpatialReference))]
[JsonSerializable(typeof(WebMapOperationalLayer))]
[JsonSerializable(typeof(WebMapOperationalLayer[]))]
[JsonSerializable(typeof(WebMapExportOptions))]
[JsonSerializable(typeof(WebMapLayoutOptions))]
[JsonSerializable(typeof(PrintExecuteResponse))]
[JsonSerializable(typeof(PrintResult))]
[JsonSerializable(typeof(PrintResult[]))]
[JsonSerializable(typeof(PrintResultValue))]
[JsonSerializable(typeof(PrintJobStatusResponse))]
[JsonSerializable(typeof(PrintJobResultRef))]
[JsonSerializable(typeof(Dictionary<string, PrintJobResultRef>))]
[JsonSerializable(typeof(PrintJobMessage))]
[JsonSerializable(typeof(PrintJobMessage[]))]
[JsonSerializable(typeof(PrintSubmitJobResponse))]
[JsonSerializable(typeof(EsriLayoutTemplateInfo))]
[JsonSerializable(typeof(EsriLayoutTemplateInfo[]))]
[JsonSerializable(typeof(EsriLayoutOptions))]
[JsonSerializable(typeof(EsriLayoutElement))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(PrintServiceInfoResponse))]
[JsonSerializable(typeof(LayoutTemplatesInfoResponse))]
[JsonSerializable(typeof(LayoutTemplatesInfoResult))]
[JsonSerializable(typeof(LayoutTemplatesInfoResult[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PrintingToolsJsonContext : JsonSerializerContext;

/// <summary>
/// Esri-canonical layout template info entry for the Get Layout Templates Info Task.
/// Fields match the documented response shape: layoutTemplate, pageSize, pageUnits,
/// webMapFrameSize, layoutOptions.
/// </summary>
internal sealed class EsriLayoutTemplateInfo
{
    /// <summary>
    /// Template name.
    /// </summary>
    public string? LayoutTemplate { get; set; }

    /// <summary>
    /// Page size as [width, height] in the units specified by pageUnits.
    /// </summary>
    public double[]? PageSize { get; set; }

    /// <summary>
    /// Units for page and frame dimensions (e.g. "inches").
    /// </summary>
    public string? PageUnits { get; set; }

    /// <summary>
    /// Map frame size as [width, height] in the units specified by pageUnits.
    /// </summary>
    public double[]? WebMapFrameSize { get; set; }

    /// <summary>
    /// Layout element visibility flags.
    /// </summary>
    public EsriLayoutOptions? LayoutOptions { get; set; }
}

/// <summary>
/// Layout element visibility flags per the Esri template info contract.
/// </summary>
internal sealed class EsriLayoutOptions
{
    /// <summary>
    /// Title text element visibility.
    /// </summary>
    public EsriLayoutElement? TitleText { get; set; }

    /// <summary>
    /// Legend element visibility.
    /// </summary>
    public EsriLayoutElement? LegendOptions { get; set; }

    /// <summary>
    /// Scale bar element visibility.
    /// </summary>
    public EsriLayoutElement? ScaleBarOptions { get; set; }

    /// <summary>
    /// Copyright text element visibility.
    /// </summary>
    public EsriLayoutElement? CopyrightText { get; set; }
}

/// <summary>
/// Describes a layout element's type and visibility.
/// </summary>
internal sealed class EsriLayoutElement
{
    /// <summary>
    /// Element type (e.g. "esriLayoutTextElement", "esriLayoutLegendElement").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Whether the element is visible in this template.
    /// </summary>
    public bool IsVisible { get; set; }
}

/// <summary>
/// Service-level metadata for the print service (choiceList for templates, formats).
/// </summary>
internal sealed class PrintServiceInfoResponse
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public PrintServiceParameter[]? Parameters { get; set; }
}

/// <summary>
/// Describes a GP service parameter.
/// </summary>
internal sealed class PrintServiceParameter
{
    public string? Name { get; set; }
    public string? DataType { get; set; }
    public string? DisplayName { get; set; }
    public string? Direction { get; set; }
    public string? DefaultValue { get; set; }
    public string[]? ChoiceList { get; set; }
}
