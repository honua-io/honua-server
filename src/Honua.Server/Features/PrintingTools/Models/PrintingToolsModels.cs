// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.PrintingTools.Models;

/// <summary>
/// Parsed web map definition from the WebMap_as_JSON parameter.
/// </summary>
internal sealed class WebMapDefinition
{
    /// <summary>
    /// Map extent defining the geographic area to render.
    /// </summary>
    public WebMapExtent? MapOptions { get; set; }

    /// <summary>
    /// Operational layers to render on the map.
    /// </summary>
    public WebMapOperationalLayer[]? OperationalLayers { get; set; }

    /// <summary>
    /// Export options including DPI and output size.
    /// </summary>
    public WebMapExportOptions? ExportOptions { get; set; }

    /// <summary>
    /// Layout options including title and other text elements.
    /// </summary>
    public WebMapLayoutOptions? LayoutOptions { get; set; }

    /// <summary>
    /// Basemap definition per the ExportWebMap spec. Captured for detection but
    /// not yet rendered — presence triggers a warning in the response.
    /// </summary>
    public JsonElement? BaseMap { get; set; }
}

/// <summary>
/// Map extent from web map definition.
/// </summary>
internal sealed class WebMapExtent
{
    /// <summary>
    /// The extent of the map to render.
    /// </summary>
    public WebMapBbox? Extent { get; set; }

    /// <summary>
    /// Scale of the map.
    /// </summary>
    public double? Scale { get; set; }

    /// <summary>
    /// Map-level spatial reference. Per the ExportWebMap spec, used as fallback
    /// when extent.spatialReference is not specified.
    /// </summary>
    public WebMapSpatialReference? SpatialReference { get; set; }
}

/// <summary>
/// Bounding box for web map extent.
/// </summary>
internal sealed class WebMapBbox
{
    public double Xmin { get; set; }
    public double Ymin { get; set; }
    public double Xmax { get; set; }
    public double Ymax { get; set; }

    /// <summary>
    /// Spatial reference for this extent.
    /// </summary>
    public WebMapSpatialReference? SpatialReference { get; set; }
}

/// <summary>
/// Spatial reference definition.
/// </summary>
internal sealed class WebMapSpatialReference
{
    public int? Wkid { get; set; }
    public int? LatestWkid { get; set; }
    public string? Wkt { get; set; }
}

/// <summary>
/// An operational layer in the web map definition.
/// </summary>
internal sealed class WebMapOperationalLayer
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public double? Opacity { get; set; }
    public bool? Visibility { get; set; }

    /// <summary>
    /// Layer ID extracted from the URL, used for local service resolution.
    /// </summary>
    [JsonIgnore]
    public int? ResolvedLayerId { get; set; }

    /// <summary>
    /// Service name extracted from the URL.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedServiceId { get; set; }
}

/// <summary>
/// Export options from the web map definition.
/// </summary>
internal sealed class WebMapExportOptions
{
    public int? Dpi { get; set; }

    /// <summary>
    /// Output size as [width, height] per the ExportWebMap specification.
    /// </summary>
    public int[]? OutputSize { get; set; }
}

/// <summary>
/// Layout options from the web map definition.
/// </summary>
internal sealed class WebMapLayoutOptions
{
    public string? TitleText { get; set; }
    public string? AuthorText { get; set; }
    public string? CopyrightText { get; set; }
    public bool? ShowLegend { get; set; }
}

/// <summary>
/// Response from the print execute endpoint.
/// </summary>
internal sealed class PrintExecuteResponse
{
    /// <summary>
    /// Results array conforming to GeoServices GP task response shape.
    /// </summary>
    public PrintResult[]? Results { get; set; }

    /// <summary>
    /// Messages array per the GP task response contract.
    /// </summary>
    public PrintJobMessage[]? Messages { get; set; }
}

/// <summary>
/// Response from the Get Layout Templates Info Task execute endpoint.
/// Returns layout template metadata in the standard GP task result shape.
/// </summary>
internal sealed class LayoutTemplatesInfoResponse
{
    /// <summary>
    /// Results array containing the layout templates info output parameter.
    /// </summary>
    public LayoutTemplatesInfoResult[]? Results { get; set; }

    /// <summary>
    /// Messages array per the GP task response contract.
    /// </summary>
    public PrintJobMessage[]? Messages { get; set; }
}

/// <summary>
/// GP result entry for the layout templates info output parameter.
/// </summary>
internal sealed class LayoutTemplatesInfoResult
{
    /// <summary>
    /// GP parameter name.
    /// </summary>
    public string? ParamName { get; set; }

    /// <summary>
    /// GP data type.
    /// </summary>
    public string? DataType { get; set; }

    /// <summary>
    /// Layout template information array in Esri-canonical shape.
    /// </summary>
    public EsriLayoutTemplateInfo[]? Value { get; set; }
}

/// <summary>
/// Individual result entry in the execute response.
/// </summary>
internal sealed class PrintResult
{
    public string? ParamName { get; set; }
    public string? DataType { get; set; }
    public PrintResultValue? Value { get; set; }
}

/// <summary>
/// Value of a print result containing the output URL.
/// </summary>
internal sealed class PrintResultValue
{
    public string? Url { get; set; }
}

/// <summary>
/// Response for job status queries following the GeoServices GP job resource contract.
/// When jobStatus is esriJobSucceeded, includes results references with paramUrl links.
/// </summary>
internal sealed class PrintJobStatusResponse
{
    public string? JobId { get; set; }
    public string? JobStatus { get; set; }
    public PrintJobMessage[]? Messages { get; set; }
    public Dictionary<string, PrintJobResultRef>? Results { get; set; }
}

/// <summary>
/// Reference to a GP result parameter, included in job status when succeeded.
/// </summary>
internal sealed class PrintJobResultRef
{
    public string? ParamUrl { get; set; }
}

/// <summary>
/// Job message entry.
/// </summary>
internal sealed class PrintJobMessage
{
    public string? Type { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Async job submission response.
/// </summary>
internal sealed class PrintSubmitJobResponse
{
    public string? JobId { get; set; }
    public string? JobStatus { get; set; }
}

/// <summary>
/// Represents a print job queued for background processing.
/// </summary>
internal sealed record PrintJob(
    string JobId,
    WebMapDefinition WebMap,
    string Format,
    string TemplateName,
    int Dpi,
    int TotalElements,
    ClaimsPrincipal? CallerPrincipal = null);

/// <summary>
/// Available output formats for print service.
/// </summary>
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
