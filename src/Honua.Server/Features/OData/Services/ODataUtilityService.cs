// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Service for handling common OData utilities including error responses, headers, and URL generation.
/// Provides shared functionality across OData operations.
/// </summary>
internal static class ODataUtilityService
{
    /// <summary>
    /// OData protocol version
    /// </summary>
    private const string ODataVersion = "4.0";

    /// <summary>
    /// OData JSON content type with minimal metadata
    /// </summary>
    private const string ODataContentType = "application/json;odata.metadata=minimal";

    private static readonly FrozenSet<string> _allowedFormats = new[]
        {
            "json",
            "application/json"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _reservedFeatureProperties = new[]
        {
            "ObjectId",
            "LayerId",
            "Geometry",
            "@odata.context",
            "@odata.type",
            "@odata.id",
            "@odata.etag",
            "Attributes"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _keyProperties = new[]
        {
            "ObjectId",
            "LayerId",
            "Id"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets required OData headers on the HTTP response.
    /// </summary>
    public static void SetODataHeaders(HttpContext context, string? etag = null)
    {
        context.Response.Headers["OData-Version"] = ODataVersion;
        if (etag != null)
        {
            context.Response.Headers.ETag = NormalizeEtagHeader(etag);
        }
    }

    /// <summary>
    /// Creates an OData v4 compliant error response using standardized error handling.
    /// See: https://docs.oasis-open.org/odata/odata-json-format/v4.0/odata-json-format-v4.0.html#sec_ErrorResponseBody
    /// </summary>
    public static IResult CreateODataError(
        HttpContext context,
        string code,
        string message,
        int statusCode = 400,
        string? target = null,
        ErrorDetail[]? details = null)
    {
        // Convert to standardized error response
        var additionalDetails = details?.Select(d => d.Message).ToList();
        var errorResponse = statusCode switch
        {
            StatusCodes.Status400BadRequest => StandardErrorResponse.BadRequest(message, additionalDetails),
            StatusCodes.Status401Unauthorized => StandardErrorResponse.Unauthorized(message, additionalDetails),
            StatusCodes.Status403Forbidden => StandardErrorResponse.Forbidden(message, additionalDetails),
            StatusCodes.Status404NotFound => StandardErrorResponse.NotFound(message, additionalDetails),
            StatusCodes.Status409Conflict => StandardErrorResponse.Conflict(message, additionalDetails),
            StatusCodes.Status500InternalServerError => StandardErrorResponse.InternalServerError(message, additionalDetails),
            StatusCodes.Status503ServiceUnavailable => StandardErrorResponse.ServiceUnavailable(message, null, additionalDetails),
            _ => new StandardErrorResponse(statusCode, code, message, additionalDetails)
        };

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates an OData error response using standardized error handling.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A standardized OData error response.</returns>
    public static IResult CreateStandardODataError(HttpContext context, string detail, int statusCode = 400, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = statusCode switch
        {
            StatusCodes.Status400BadRequest => StandardErrorResponse.BadRequest(detail, additionalDetails),
            StatusCodes.Status401Unauthorized => StandardErrorResponse.Unauthorized(detail, additionalDetails),
            StatusCodes.Status403Forbidden => StandardErrorResponse.Forbidden(detail, additionalDetails),
            StatusCodes.Status404NotFound => StandardErrorResponse.NotFound(detail, additionalDetails),
            StatusCodes.Status409Conflict => StandardErrorResponse.Conflict(detail, additionalDetails),
            StatusCodes.Status500InternalServerError => StandardErrorResponse.InternalServerError(detail, additionalDetails),
            StatusCodes.Status503ServiceUnavailable => StandardErrorResponse.ServiceUnavailable(detail, null, additionalDetails),
            _ => new StandardErrorResponse(statusCode, "Error", detail, additionalDetails)
        };

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Generates the @odata.nextLink URL for pagination support.
    /// </summary>
    public static string GenerateNextLink(
        HttpRequest request,
        int nextSkip,
        int top,
        string? filter,
        string? select,
        string? orderby,
        bool? count,
        string? expand = null)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var queryParams = new List<string>
        {
            $"$skip={nextSkip}",
            $"$top={top}"
        };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        }

        if (!string.IsNullOrWhiteSpace(select))
        {
            queryParams.Add($"$select={Uri.EscapeDataString(select)}");
        }

        if (!string.IsNullOrWhiteSpace(orderby))
        {
            queryParams.Add($"$orderby={Uri.EscapeDataString(orderby)}");
        }

        if (count == true)
        {
            queryParams.Add("$count=true");
        }

        if (!string.IsNullOrWhiteSpace(expand))
        {
            queryParams.Add($"$expand={Uri.EscapeDataString(expand)}");
        }

        return $"{baseUrl}{request.Path}?{string.Join("&", queryParams)}";
    }

    /// <summary>
    /// Gets a timeout-aware cancellation token from the HTTP context.
    /// Prefers the limits timeout token if available, otherwise uses request cancellation.
    /// </summary>
    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }

    /// <summary>
    /// Builds an OData context URL for the given base URL and entity type.
    /// </summary>
    public static string BuildContextUrl(
        string baseUrl,
        string entityType,
        bool isSingle = false,
        string? select = null,
        string? expand = null)
    {
        var segment = entityType;
        var selection = BuildContextSelection(select, expand);
        if (!string.IsNullOrWhiteSpace(selection))
        {
            segment = $"{segment}({selection})";
        }

        var suffix = isSingle ? "/$entity" : "";
        return $"{baseUrl}/odata/$metadata#{segment}{suffix}";
    }

    /// <summary>
    /// Creates a standardized OData response with proper context and metadata.
    /// </summary>
    public static ODataResponse CreateODataResponse(
        string baseUrl,
        string entityType,
        object[] value,
        long? totalCount = null,
        string? nextLink = null,
        string? select = null,
        string? expand = null)
    {
        return new ODataResponse
        {
            Context = BuildContextUrl(baseUrl, entityType, isSingle: false, select, expand),
            Count = totalCount,
            NextLink = nextLink,
            Value = value
        };
    }

    /// <summary>
    /// Creates a standardized OData feature response for single feature operations.
    /// </summary>
    public static ODataFeatureResponse CreateODataFeatureResponse(
        string baseUrl,
        long objectId,
        int layerId,
        ODataSpatialGeometry? geometry,
        Dictionary<string, object?> attributes)
    {
        return new ODataFeatureResponse
        {
            Context = BuildContextUrl(baseUrl, "Features", isSingle: true),
            ObjectId = objectId,
            LayerId = layerId,
            Geometry = geometry,
            Attributes = attributes
        };
    }

    /// <summary>
    /// Extracts the base URL from an HTTP request.
    /// </summary>
    public static string GetBaseUrl(HttpRequest request)
    {
        return BaseUrlResolver.GetBaseUrl(request);
    }

    /// <summary>
    /// Gets the OData content type for responses.
    /// </summary>
    public static string GetODataContentType()
    {
        return ODataContentType;
    }

    public static IReadOnlySet<string> GetAllowedFormats()
    {
        return _allowedFormats;
    }

    /// <summary>
    /// Checks if pagination should be applied based on result size and current parameters.
    /// </summary>
    public static bool ShouldPaginate(int resultCount, int currentSkip, long totalCount, int? top)
    {
        return (long)currentSkip + resultCount < totalCount;
    }

    /// <summary>
    /// Calculates the next skip value for pagination.
    /// </summary>
    public static int CalculateNextSkip(int currentSkip, int? top)
    {
        var currentTop = top ?? 1000;
        return currentSkip + currentTop;
    }

    /// <summary>
    /// Creates location header value for created resources.
    /// </summary>
    public static string CreateLocationHeader(string baseUrl, int layerId, long objectId)
    {
        return $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={objectId})";
    }

    /// <summary>
    /// Creates OData-EntityId header value for created resources.
    /// </summary>
    public static string CreateODataEntityId(string baseUrl, int layerId, long objectId)
    {
        return $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={objectId})";
    }

    /// <summary>
    /// Validates that a layer ID is positive.
    /// </summary>
    public static bool IsValidLayerId(int layerId)
    {
        return layerId > 0;
    }

    /// <summary>
    /// Validates that an object ID is positive.
    /// </summary>
    public static bool IsValidObjectId(long objectId)
    {
        return objectId > 0;
    }

    /// <summary>
    /// Gets the proper HTTP status code for an OData CRUD result.
    /// </summary>
    public static int GetStatusCodeForCrudResult<T>(ODataCrudResult<T> result)
    {
        return result.StatusCode;
    }

    /// <summary>
    /// Creates an IResult from an OData CRUD result with proper headers.
    /// </summary>
    public static IResult CreateResultFromCrudResult<T>(
        HttpContext context,
        ODataCrudResult<T> crudResult)
    {
        if (!crudResult.IsSuccess)
        {
            var code = MapODataCode(crudResult.StatusCode);
            return CreateODataError(context, code, crudResult.ErrorMessage ?? "An error occurred", crudResult.StatusCode);
        }

        SetODataHeaders(context, crudResult.ETag);

        if (crudResult.LocationHeader != null)
        {
            context.Response.Headers.Location = crudResult.LocationHeader;
            context.Response.Headers["OData-EntityId"] = crudResult.LocationHeader;
        }

        if (crudResult.StatusCode == 204)
        {
            return Results.NoContent();
        }

        if (crudResult.Data is Dictionary<string, object?> dictionary)
        {
            return Results.Json(
                dictionary,
                ODataJsonContext.Default.DictionaryStringObject,
                contentType: ODataContentType,
                statusCode: crudResult.StatusCode);
        }

        return Results.Json(
            crudResult.Data,
            GetJsonTypeInfo<T>(),
            contentType: ODataContentType,
            statusCode: crudResult.StatusCode);
    }

    /// <summary>
    /// Gets the appropriate JsonTypeInfo for serialization based on type.
    /// </summary>
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> GetJsonTypeInfo<T>()
    {
        // This would need to be expanded based on the actual types used
        // For now, return a generic JsonTypeInfo
        return (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(object)ODataJsonContext.Default.ODataFeatureResponse;
    }

    public static Dictionary<string, object?> BuildFeaturePayload(
        int layerId,
        Feature feature,
        ODataSpatialGeometry? geometry,
        IReadOnlyDictionary<string, object?> attributes)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectId"] = feature.Id,
            ["LayerId"] = layerId,
            ["Geometry"] = geometry
        };

        foreach (var (key, value) in attributes)
        {
            if (IsReservedFeatureProperty(key))
            {
                continue;
            }

            payload[key] = value;
        }

        return payload;
    }

    public static Dictionary<string, object?> BuildLayerPayload(LayerDefinition layer)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = layer.Id,
            ["Name"] = layer.Name,
            ["Description"] = layer.Description,
            ["GeometryType"] = layer.GeometryType.ToString(),
            ["Srid"] = layer.SpatialReference.ToSrid()
        };
    }

    public static HashSet<string>? ParseSelect(string? select)
    {
        if (string.IsNullOrWhiteSpace(select))
        {
            return null;
        }

        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return fields.Contains("*") ? null : fields;
    }

    private static string? BuildContextSelection(string? select, string? expand)
    {
        var selections = new List<string>();

        if (!string.IsNullOrWhiteSpace(select))
        {
            selections.AddRange(select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !item.Equals("*", StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(expand))
        {
            selections.AddRange(expand.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return selections.Count == 0 ? null : string.Join(",", selections.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static Dictionary<string, object?> ApplySelect(Dictionary<string, object?> payload, string select)
    {
        var fields = ParseSelect(select);
        if (fields == null)
        {
            return payload;
        }

        var filtered = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in payload)
        {
            if (key.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase))
            {
                filtered[key] = value;
                continue;
            }

            if (IsKeyProperty(key) || fields.Contains(key))
            {
                filtered[key] = value;
            }
        }

        return filtered;
    }

    public static bool IsReservedFeatureProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return true;
        }

        return _reservedFeatureProperties.Contains(propertyName);
    }

    public static bool IsKeyProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return _keyProperties.Contains(propertyName);
    }

    public static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Identifier";
        }

        var sb = new System.Text.StringBuilder();
        var started = false;

        foreach (var c in name)
        {
            if (!started && char.IsLetter(c))
            {
                sb.Append(c);
                started = true;
            }
            else if (started && (char.IsLetterOrDigit(c) || c == '_'))
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "Identifier";
    }

    private static string MapODataCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "ResourceNotFound",
        StatusCodes.Status408RequestTimeout => "RequestTimeout",
        StatusCodes.Status413PayloadTooLarge => "PayloadTooLarge",
        StatusCodes.Status429TooManyRequests => "TooManyRequests",
        StatusCodes.Status412PreconditionFailed => "PreconditionFailed",
        StatusCodes.Status500InternalServerError => "InternalServerError",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        _ => "Error"
    };

    /// <summary>
    /// Determines if an HTTP method supports a request body.
    /// </summary>
    public static bool MethodSupportsBody(string method)
    {
        return method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
               method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
               method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldReturnMinimal(string? preferHeader)
    {
        if (string.IsNullOrWhiteSpace(preferHeader))
        {
            return false;
        }

        return preferHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => token.Equals("return=minimal", StringComparison.OrdinalIgnoreCase));
    }

    public static object? NormalizeForEtag(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in readOnlyDict)
            {
                sorted[kvp.Key] = NormalizeForEtag(kvp.Value);
            }

            return sorted;
        }

        if (value is IDictionary<string, object?> dict)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dict)
            {
                sorted[kvp.Key] = NormalizeForEtag(kvp.Value);
            }

            return sorted;
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeForEtag(item));
            }

            return list.ToArray();
        }

        return value;
    }

    private static string NormalizeEtagHeader(string etag)
    {
        var trimmed = etag.Trim();
        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[2..].TrimStart();
            if (rest.StartsWith('"') && rest.EndsWith('"'))
            {
                return $"W/{rest}";
            }

            return $"W/\"{rest}\"";
        }

        if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            return trimmed;
        }

        return $"\"{trimmed}\"";
    }

    /// <summary>
    /// Validates an OData feature request for required fields.
    /// </summary>
    public static (bool isValid, string? errorMessage) ValidateFeatureRequest(ODataFeatureRequest? request, string method)
    {
        if (!MethodSupportsBody(method))
        {
            return (true, null); // No body validation needed for GET/DELETE
        }

        if (request == null)
        {
            return (false, "Request body is required for this operation.");
        }

        // Additional validation could be added here based on layer schema
        return (true, null);
    }
}
