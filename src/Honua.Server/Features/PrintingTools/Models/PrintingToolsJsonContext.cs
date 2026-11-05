// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.PrintingTools.Models;

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
[JsonSerializable(typeof(GeoServicesLayoutTemplateInfo))]
[JsonSerializable(typeof(GeoServicesLayoutTemplateInfo[]))]
[JsonSerializable(typeof(GeoServicesLayoutOptions))]
[JsonSerializable(typeof(GeoServicesLayoutElement))]
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

internal sealed class GeoServicesLayoutTemplateInfo
{
    public string? LayoutTemplate { get; set; }

    public double[]? PageSize { get; set; }

    public string? PageUnits { get; set; }

    public double[]? WebMapFrameSize { get; set; }

    public GeoServicesLayoutOptions? LayoutOptions { get; set; }
}

internal sealed class GeoServicesLayoutOptions
{
    public GeoServicesLayoutElement? TitleText { get; set; }

    public GeoServicesLayoutElement? LegendOptions { get; set; }

    public GeoServicesLayoutElement? ScaleBarOptions { get; set; }

    public GeoServicesLayoutElement? CopyrightText { get; set; }
}

internal sealed class GeoServicesLayoutElement
{
    public string? Type { get; set; }

    public bool IsVisible { get; set; }
}

internal sealed class PrintServiceInfoResponse
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public PrintServiceParameter[]? Parameters { get; set; }
}

internal sealed class PrintServiceParameter
{
    public string? Name { get; set; }
    public string? DataType { get; set; }
    public string? DisplayName { get; set; }
    public string? Direction { get; set; }
    public string? DefaultValue { get; set; }
    public string[]? ChoiceList { get; set; }
}
