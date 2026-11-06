// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.PrintingTools.Models;

internal sealed class WebMapDefinition
{
    public WebMapExtent? MapOptions { get; set; }

    public WebMapOperationalLayer[]? OperationalLayers { get; set; }

    public WebMapExportOptions? ExportOptions { get; set; }

    public WebMapLayoutOptions? LayoutOptions { get; set; }

    public JsonElement? BaseMap { get; set; }
}

internal sealed class WebMapExtent
{
    public WebMapBbox? Extent { get; set; }

    public double? Scale { get; set; }

    public WebMapSpatialReference? SpatialReference { get; set; }
}

internal sealed class WebMapBbox
{
    public double Xmin { get; set; }
    public double Ymin { get; set; }
    public double Xmax { get; set; }
    public double Ymax { get; set; }

    public WebMapSpatialReference? SpatialReference { get; set; }
}

internal sealed class WebMapSpatialReference
{
    public int? Wkid { get; set; }
    public int? LatestWkid { get; set; }
    public string? Wkt { get; set; }
}

internal sealed class WebMapOperationalLayer
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public double? Opacity { get; set; }
    public bool? Visibility { get; set; }

    [JsonIgnore]
    public int? ResolvedLayerId { get; set; }

    [JsonIgnore]
    public string? ResolvedServiceId { get; set; }
}

internal sealed class WebMapExportOptions
{
    public int? Dpi { get; set; }

    public int[]? OutputSize { get; set; }
}

internal sealed class WebMapLayoutOptions
{
    public string? TitleText { get; set; }
    public string? AuthorText { get; set; }
    public string? CopyrightText { get; set; }
    public bool? ShowLegend { get; set; }
}

internal sealed class PrintExecuteResponse
{
    public PrintResult[]? Results { get; set; }

    public PrintJobMessage[]? Messages { get; set; }
}

internal sealed class LayoutTemplatesInfoResponse
{
    public LayoutTemplatesInfoResult[]? Results { get; set; }

    public PrintJobMessage[]? Messages { get; set; }
}

internal sealed class LayoutTemplatesInfoResult
{
    public string? ParamName { get; set; }

    public string? DataType { get; set; }

    public GeoServicesLayoutTemplateInfo[]? Value { get; set; }
}

internal sealed class PrintResult
{
    public string? ParamName { get; set; }
    public string? DataType { get; set; }
    public PrintResultValue? Value { get; set; }
}

internal sealed class PrintResultValue
{
    public string? Url { get; set; }
}

internal sealed class PrintJobStatusResponse
{
    public string? JobId { get; set; }
    public string? JobStatus { get; set; }
    public PrintJobMessage[]? Messages { get; set; }
    public Dictionary<string, PrintJobResultRef>? Results { get; set; }
}

internal sealed class PrintJobResultRef
{
    public string? ParamUrl { get; set; }
}

internal sealed class PrintJobMessage
{
    public string? Type { get; set; }
    public string? Description { get; set; }
}

internal sealed class PrintSubmitJobResponse
{
    public string? JobId { get; set; }
    public string? JobStatus { get; set; }
}

internal sealed record PrintJob(
    string JobId,
    WebMapDefinition WebMap,
    string Format,
    string TemplateName,
    int Dpi,
    int TotalElements,
    JobSecurityContext? SubmitterSecurityContext = null);

internal static class PrintOutputFormat
{
    public const string Pdf = "PDF";
    public const string Png32 = "PNG32";
    public const string Jpg = "JPG";

    public static bool IsSupported(string? format) => format?.ToUpperInvariant() switch
    {
        Pdf or Png32 or Jpg => true,
        _ => false
    };

    public static string GetContentType(string format) => format.ToUpperInvariant() switch
    {
        Pdf => "application/pdf",
        Png32 => "image/png",
        Jpg => "image/jpeg",
        _ => "application/octet-stream"
    };

    public static string GetExtension(string format) => format.ToUpperInvariant() switch
    {
        Pdf => ".pdf",
        Png32 => ".png",
        Jpg => ".jpg",
        _ => ".bin"
    };
}
