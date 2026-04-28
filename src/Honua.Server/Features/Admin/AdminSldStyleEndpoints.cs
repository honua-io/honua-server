// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Xml;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Styling.Sld;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for SLD (Styled Layer Descriptor) import/export. Migration tooling
/// only — Community edition. No license gating is applied.
/// </summary>
internal static class AdminSldStyleEndpoints
{
    private const int MaxSldBytes = 1_048_576; // 1 MiB cap; aligns with SldParser.

    public static void MapAdminSldStyleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Styles", "Migration")
            .RequireAdminAuthorization();

        _ = group.MapPost("/{layerId:int}/style/import-sld", HandleImportSld)
            .WithName("ImportLayerSldStyle")
            .WithSummary("Import an SLD/SE document and convert it to a MapLibre style.")
            .Accepts<string>("application/xml", "text/xml")
            .Produces<ApiResponse<SldImportResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces<ApiResponse<SldImportFailureResponse>>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        _ = group.MapGet("/{layerId:int}/style/export-sld", HandleExportSld)
            .WithName("ExportLayerSldStyle")
            .WithSummary("Export the stored MapLibre style as an SLD 1.0 document.")
            .Produces<string>(StatusCodes.Status200OK, "application/xml")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> HandleImportSld(
        int layerId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] ILayerStyleService styleService,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        [FromServices] ILogger<SldImportLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                statusCode,
                layerResult.ErrorMessage ?? $"Layer {layerId} not found.");
        }

        if (context.Request.ContentLength is { } length && length > MaxSldBytes)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status413PayloadTooLarge,
                $"SLD payload exceeds the {MaxSldBytes}-byte limit.");
        }

        string sldXml;
        try
        {
            sldXml = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status413PayloadTooLarge, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(sldXml))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "SLD request body must not be empty.");
        }

        SldDocument parsed;
        try
        {
            parsed = SldParser.Parse(sldXml);
        }
        catch (SldParseException ex)
        {
            SldStyleLog.ParseFailed(logger, layerId, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ex.Message);
        }
        catch (XmlException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "SLD document is not well-formed XML.");
        }

        var conversion = SldToMapLibreConverter.Convert(parsed);

        if (conversion.HasErrors || conversion.Layers.Length == 0)
        {
            var diagnostics = conversion.Layers.Length == 0 && !conversion.HasErrors
                ? AppendError(conversion.Diagnostics,
                    "MapLibreLayers",
                    "SLD document contained no convertible symbolizers.")
                : conversion.Diagnostics;

            var failure = new SldImportFailureResponse
            {
                DetectedVersion = conversion.DetectedVersion.ToString(),
                Diagnostics = diagnostics
            };

            SldStyleLog.ImportRejected(logger, layerId, diagnostics.Length);
            return Results.Json(
                ApiResponse<SldImportFailureResponse>.Failure("SLD import failed; see diagnostics.", failure),
                SldStyleJsonContext.Default.ApiResponseSldImportFailureResponse,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var styleJson = BuildMapLibreStyleDocument(layerResult.Resource, conversion.Layers);
        var styleElement = JsonSerializer.SerializeToElement(styleJson, StyleJsonContext.Default.DictionaryStringObject);

        var update = await styleService.UpdateStyleAsync(
                layerResult.Resource,
                styleElement,
                drawingInfo: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (update.Status == LayerStyleUpdateStatus.Invalid)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                update.ErrorMessage ?? "Converted SLD style failed MapLibre validation.");
        }

        if (update.Status == LayerStyleUpdateStatus.NotFound || update.Style == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Style for layer {layerId} not found.");
        }

        await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken).ConfigureAwait(false);

        var response = new SldImportResponse
        {
            DetectedVersion = conversion.DetectedVersion.ToString(),
            LayerCount = conversion.Layers.Length,
            MapLibreStyle = update.Style.MapLibreStyle,
            Diagnostics = conversion.Diagnostics
        };

        SldStyleLog.ImportSucceeded(
            logger,
            layerId,
            conversion.DetectedVersion.ToString(),
            conversion.Layers.Length,
            conversion.Diagnostics.Length);

        return Results.Json(
            ApiResponse<SldImportResponse>.CreateSuccess(response),
            SldStyleJsonContext.Default.ApiResponseSldImportResponse);
    }

    private static async Task<IResult> HandleExportSld(
        int layerId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] ILayerStyleService styleService,
        [FromServices] ILogger<SldImportLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                statusCode,
                layerResult.ErrorMessage ?? $"Layer {layerId} not found.");
        }

        var snapshot = await styleService.GetStyleAsync(layerResult.Resource, cancellationToken).ConfigureAwait(false);
        if (snapshot == null || !snapshot.MapLibreStyle.HasValue || snapshot.MapLibreStyle.Value.ValueKind != JsonValueKind.Object)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Style for layer {layerId} not found.");
        }

        if (!snapshot.MapLibreStyle.Value.TryGetProperty("layers", out var layersElement)
            || layersElement.ValueKind != JsonValueKind.Array)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "Stored MapLibre style is missing a layers array.");
        }

        MapLibreStyleLayer[] layers;
        try
        {
            layers = JsonSerializer.Deserialize(
                layersElement.GetRawText(),
                MapLibreStyleJsonContext.Default.MapLibreStyleLayerArray) ?? Array.Empty<MapLibreStyleLayer>();
        }
        catch (JsonException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "Stored MapLibre style cannot be deserialized for export.");
        }

        var layerName = layerResult.Resource.Name ?? $"layer-{layerId}";
        var export = MapLibreToSldConverter.Export(layers, layerName);

        if (export.Diagnostics.Any(d => d.Severity == SldDiagnosticSeverity.Error))
        {
            var failure = new SldImportFailureResponse
            {
                Diagnostics = export.Diagnostics
            };

            return Results.Json(
                ApiResponse<SldImportFailureResponse>.Failure("Stored style cannot be exported to SLD; see diagnostics.", failure),
                SldStyleJsonContext.Default.ApiResponseSldImportFailureResponse,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var diagnosticCount = export.Diagnostics.Length;
        context.Response.Headers["X-Sld-Diagnostic-Count"] = diagnosticCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (diagnosticCount > 0)
        {
            context.Response.Headers["X-Sld-Diagnostics"] = JsonSerializer.Serialize(
                export.Diagnostics,
                SldStyleJsonContext.Default.SldConversionDiagnosticArray);
        }

        SldStyleLog.ExportSucceeded(logger, layerId, layers.Length, diagnosticCount);

        return Results.Content(export.SldXml, "application/xml", Encoding.UTF8);
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var totalRead = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > MaxSldBytes)
            {
                throw new InvalidDataException($"SLD payload exceeds the {MaxSldBytes}-byte limit.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SldConversionDiagnostic[] AppendError(
        SldConversionDiagnostic[] existing,
        string construct,
        string message)
    {
        var combined = new SldConversionDiagnostic[existing.Length + 1];
        Array.Copy(existing, combined, existing.Length);
        combined[^1] = new SldConversionDiagnostic
        {
            Severity = SldDiagnosticSeverity.Error,
            Construct = construct,
            Message = message
        };
        return combined;
    }

    private static Dictionary<string, object?> BuildMapLibreStyleDocument(
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        MapLibreStyleLayer[] convertedLayers)
    {
        var document = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var sourceId = StyleDefaults.GetSourceId(layer);
        var layerList = new List<Dictionary<string, object?>>(convertedLayers.Length);

        foreach (var converted in convertedLayers)
        {
            var serialized = JsonSerializer.SerializeToElement(
                converted,
                MapLibreStyleJsonContext.Default.MapLibreStyleLayer);
            var layerObject = JsonElementToDictionary(serialized);
            layerObject["source"] = sourceId;
            layerObject["source-layer"] = StyleDefaults.SourceLayerName;
            layerList.Add(layerObject);
        }

        document["layers"] = layerList;
        return document;
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = JsonElementToObject(property.Value);
        }

        return dict;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonElementToDictionary(element),
        JsonValueKind.Array => JsonElementToList(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var i) ? i : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => null
    };

    private static List<object?> JsonElementToList(JsonElement element)
    {
        var list = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            list.Add(JsonElementToObject(item));
        }

        return list;
    }
}

/// <summary>
/// Marker type for the SLD log category.
/// </summary>
internal sealed class SldImportLogCategory;

internal static partial class SldStyleLog
{
    [LoggerMessage(8100, LogLevel.Debug, "SLD parse failed for layer {LayerId}: {Reason}")]
    public static partial void ParseFailed(ILogger logger, int layerId, string reason);

    [LoggerMessage(8101, LogLevel.Information, "SLD import succeeded for layer {LayerId} (version={SldVersion}, layers={LayerCount}, diagnostics={DiagnosticCount})")]
    public static partial void ImportSucceeded(ILogger logger, int layerId, string sldVersion, int layerCount, int diagnosticCount);

    [LoggerMessage(8102, LogLevel.Information, "SLD import rejected for layer {LayerId} with {DiagnosticCount} diagnostics")]
    public static partial void ImportRejected(ILogger logger, int layerId, int diagnosticCount);

    [LoggerMessage(8103, LogLevel.Information, "SLD export produced for layer {LayerId} (layers={LayerCount}, diagnostics={DiagnosticCount})")]
    public static partial void ExportSucceeded(ILogger logger, int layerId, int layerCount, int diagnosticCount);
}
