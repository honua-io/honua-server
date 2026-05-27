// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.OData.Models;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Service for handling common OData utilities including error responses, headers, and URL generation.
/// Provides shared functionality across OData operations.
/// </summary>
internal static class ODataUtilityService
{
    internal const string TrackChangesContinuationParameter = "honua_track_changes";
    internal const string TrackChangesSnapshotParameter = "honua_track_changes_snapshot";
    internal const string TrackChangesPreferenceValue = "odata.track-changes";

    /// <summary>
    /// OData JSON content type with minimal metadata
    /// </summary>
    private const string ODataMediaType = "application/json";
    private const string ODataMetadataMinimal = "minimal";
    private const string ODataMetadataFull = "full";
    private const string ODataMetadataNone = "none";
    private const string ODataContentType = $"{ODataMediaType};metadata={ODataMetadataMinimal}";

    private static readonly FrozenSet<string> _allowedFormats = new[]
        {
            "json",
            "application/json",
            "json;metadata=minimal",
            "json;metadata=none",
            "json;odata.metadata=minimal",
            "json;odata.metadata=none",
            "application/json;metadata=minimal",
            "application/json;metadata=none",
            "application/json;odata.metadata=minimal",
            "application/json;odata.metadata=none"
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
        ODataProtocolHeaders.SetVersionHeader(context);
        if (etag != null)
        {
            context.Response.Headers.ETag = NormalizeEtagHeader(etag);
        }
    }

    /// <summary>
    /// Creates an OData v4 compliant error response using standardized error handling.
    /// See: https://docs.oasis-open.org/odata/odata-json-format/v4.01/odata-json-format-v4.01.html#sec_ErrorResponseBody
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

        return StandardErrorResponseFormatter.FormatError(
            context,
            errorResponse,
            new ErrorResponseFormatterOptions
            {
                ODataErrorCode = code
            });
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
    /// When useSkipToken is true, the skip value is encoded as an opaque Base64Url cursor
    /// that includes a query fingerprint to prevent token reuse across different queries.
    /// </summary>
    public static string GenerateNextLink(
        HttpRequest request,
        int nextSkip,
        int top,
        string? filter,
        string? select,
        string? orderby,
        bool? count,
        string? expand = null,
        bool useSkipToken = false,
        string? compute = null,
        string? format = null,
        bool trackChanges = false,
        ODataDeltaService.DeltaQueryState? trackChangesState = null)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var queryParams = new List<string>();

        if (useSkipToken)
        {
            var opaqueToken = ODataSkipTokenService.Encode(nextSkip, filter, orderby);
            queryParams.Add($"$skiptoken={Uri.EscapeDataString(opaqueToken)}");
        }
        else
        {
            queryParams.Add($"$skip={nextSkip}");
        }

        queryParams.Add($"$top={top}");

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

        if (!string.IsNullOrWhiteSpace(compute))
        {
            queryParams.Add($"$compute={Uri.EscapeDataString(compute)}");
        }

        if (!string.IsNullOrWhiteSpace(format))
        {
            queryParams.Add($"$format={Uri.EscapeDataString(format)}");
        }

        if (trackChanges)
        {
            queryParams.Add($"{TrackChangesContinuationParameter}=true");
            if (trackChangesState != null)
            {
                queryParams.Add($"{TrackChangesSnapshotParameter}={Uri.EscapeDataString(ODataDeltaService.Encode(trackChangesState))}");
            }
        }

        return $"{baseUrl}{request.Path}?{string.Join("&", queryParams)}";
    }

    /// <summary>
    /// Generates a @odata.deltaLink URL for change tracking support.
    /// </summary>
    public static string GenerateDeltaLink(
        HttpRequest request,
        ODataDeltaService.DeltaQueryState state)
    {
        return ODataDeltaService.GenerateDeltaLink(
            request,
            BaseUrlResolver.GetBaseUrl(request),
            state);
    }

    /// <summary>
    /// Generates a paged next link for delta responses.
    /// </summary>
    public static string GenerateDeltaNextLink(
        HttpRequest request,
        ODataDeltaService.DeltaQueryState deltaState,
        int nextSkip,
        int top,
        bool useSkipToken,
        string? filter,
        string? orderby)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var queryParams = new List<string>
        {
            $"$deltatoken={Uri.EscapeDataString(ODataDeltaService.Encode(deltaState))}",
            $"$top={top}"
        };

        if (useSkipToken)
        {
            queryParams.Add($"$skiptoken={Uri.EscapeDataString(ODataSkipTokenService.Encode(nextSkip, filter, orderby))}");
        }
        else
        {
            queryParams.Add($"$skip={nextSkip}");
        }

        return $"{baseUrl}{request.Path}?{string.Join("&", queryParams)}";
    }

    /// <summary>
    /// Gets a timeout-aware cancellation token from the HTTP context.
    /// Prefers the limits timeout token if available, otherwise uses request cancellation.
    /// </summary>
    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
        => TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

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
        string? expand = null,
        bool includeContext = true)
    {
        return new ODataResponse
        {
            Context = includeContext ? BuildContextUrl(baseUrl, entityType, isSingle: false, select, expand) : null,
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

    public static string GetODataContentType(HttpRequest request, string? format)
    {
        var metadataLevel = ResolveMetadataLevel(request, format);
        return $"{ODataMediaType};metadata={metadataLevel}";
    }

    public static bool HasTrackChangesPreference(string? preferHeader)
    {
        if (string.IsNullOrWhiteSpace(preferHeader))
        {
            return false;
        }

        foreach (var token in preferHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals(TrackChangesPreferenceValue, StringComparison.OrdinalIgnoreCase) ||
                token.Equals("track-changes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool RequestsTrackChanges(HttpRequest request)
    {
        if (HasTrackChangesPreference(request.Headers["Prefer"].ToString()))
        {
            return true;
        }

        if (request.Query.TryGetValue(TrackChangesContinuationParameter, out var values))
        {
            var raw = values.ToString();
            return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static void ApplyTrackChangesPreference(HttpContext context)
    {
        context.Response.Headers["Preference-Applied"] = TrackChangesPreferenceValue;
    }

    public static bool ShouldIncludeContext(HttpRequest request, string? format)
        => !string.Equals(
            ResolveMetadataLevel(request, format),
            ODataMetadataNone,
            StringComparison.OrdinalIgnoreCase);

    public static IReadOnlySet<string> GetAllowedFormats()
    {
        return _allowedFormats;
    }

    public static bool TryGetUnsupportedMetadataLevel(HttpRequest request, string? format, out string level)
    {
        level = string.Empty;

        if (TryResolveMetadataLevel(format, out var formatLevel))
        {
            if (IsUnsupportedMetadataLevel(formatLevel))
            {
                level = formatLevel;
                return true;
            }

            // $format takes precedence over Accept when present and parseable.
            return false;
        }

        var acceptHeader = request.Headers.Accept.ToString();
        if (TryResolveMetadataLevelFromAccept(acceptHeader, out _))
        {
            // A supported Accept metadata level is available; negotiate that level.
            return false;
        }

        if (TryResolveUnsupportedMetadataLevelFromAccept(acceptHeader, out var acceptLevel))
        {
            level = acceptLevel;
            return true;
        }

        return false;
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
        ODataCrudResult<T> crudResult,
        string? format = null)
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
            context.Response.Headers["EntityId"] = crudResult.LocationHeader;
        }

        if (crudResult.StatusCode == 204)
        {
            return Results.NoContent();
        }

        var contentType = GetODataContentType(context.Request, format);
        if (crudResult.Data is Dictionary<string, object?> dictionary)
        {
            return Results.Json(
                dictionary,
                ODataJsonContext.Default.DictionaryStringObject,
                contentType: contentType,
                statusCode: crudResult.StatusCode);
        }

        if (crudResult.Data is null)
        {
            return Results.StatusCode(crudResult.StatusCode);
        }

        return Results.Json(
            (object)crudResult.Data,
            ODataJsonContext.Default.Object,
            contentType: contentType,
            statusCode: crudResult.StatusCode);
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

    /// <summary>
    /// Projects the OData Layer entity directly off the canonical resource and the
    /// publication's layer index.
    /// </summary>
    public static Dictionary<string, object?> BuildLayerPayload(
        MetadataV2Resource resource,
        int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = layerIndex,
            ["Name"] = resource.Metadata.Name,
            ["Description"] = resource.Metadata.Description,
            ["GeometryType"] = resource.ReadGeometryType().ToString(),
            ["Srid"] = resource.ReadSrid() ?? 0
        };
    }

    /// <summary>
    /// Metadata v2 overload of <see cref="BuildLayerPayload(LayerDefinition)"/>.
    /// Projects the OData Layer entity directly off the canonical resource and the
    /// publication's layer index, without going through the v1 LayerDefinition shape.
    /// </summary>
    public static Dictionary<string, object?> BuildLayerPayload(
        MetadataV2Resource resource,
        int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = layerIndex,
            ["Name"] = resource.Metadata.Name,
            ["Description"] = resource.Metadata.Description,
            ["GeometryType"] = resource.ReadGeometryType().ToString(),
            ["Srid"] = resource.ReadSrid() ?? 0
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

        if (FeatureAttributeVisibility.IsInternalAttribute(propertyName))
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

    public static string BuildRelationshipMetadataName(string relationshipName, int relationshipId)
    {
        var sanitized = SanitizeIdentifier(relationshipName);
        return $"{sanitized}_r{relationshipId}";
    }

    /// <summary>
    /// V2 overload of <see cref="BuildRelationshipMetadataName(string, int)"/> where the
    /// relationship identifier is the canonical string id from
    /// <see cref="MetadataV2Relationship.Id"/> rather than the v1 integer id.
    /// </summary>
    public static string BuildRelationshipMetadataNameForV2(string relationshipName, string relationshipId)
    {
        var sanitizedName = SanitizeIdentifier(relationshipName);
        var sanitizedId = SanitizeIdentifier(relationshipId);
        return $"{sanitizedName}_r{sanitizedId}";
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

    private static bool TryResolveMetadataLevel(string? format, out string level)
    {
        level = ODataMetadataMinimal;
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        var mediaType = format.Trim();
        if (string.Equals(mediaType, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, ODataMediaType, StringComparison.OrdinalIgnoreCase))
        {
            level = ODataMetadataMinimal;
            return true;
        }

        return TryResolveMetadataLevelFromMediaType(mediaType, out level);
    }

    private static bool TryResolveMetadataLevelFromAccept(string? acceptHeader, out string level)
    {
        level = ODataMetadataMinimal;
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return false;
        }

        var selectedLevel = string.Empty;
        var selectedQuality = double.MinValue;

        var mediaTypes = acceptHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var mediaType in mediaTypes)
        {
            if (!TryResolveMetadataLevelFromMediaType(mediaType, out var candidateLevel))
            {
                continue;
            }

            if (IsUnsupportedMetadataLevel(candidateLevel))
            {
                continue;
            }

            var candidateQuality = ResolveQualityFactor(mediaType);
            if (candidateQuality <= 0)
            {
                continue;
            }

            if (candidateQuality > selectedQuality)
            {
                selectedQuality = candidateQuality;
                selectedLevel = candidateLevel;
            }
        }

        if (selectedQuality < 0 || string.IsNullOrEmpty(selectedLevel))
        {
            return false;
        }

        level = selectedLevel;
        return true;
    }

    private static bool TryResolveUnsupportedMetadataLevelFromAccept(string? acceptHeader, out string level)
    {
        level = string.Empty;
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return false;
        }

        var selectedLevel = string.Empty;
        var selectedQuality = double.MinValue;

        var mediaTypes = acceptHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var mediaType in mediaTypes)
        {
            if (!TryResolveMetadataLevelFromMediaType(mediaType, out var candidateLevel) ||
                !IsUnsupportedMetadataLevel(candidateLevel))
            {
                continue;
            }

            var candidateQuality = ResolveQualityFactor(mediaType);
            if (candidateQuality <= 0)
            {
                continue;
            }

            if (candidateQuality > selectedQuality)
            {
                selectedQuality = candidateQuality;
                selectedLevel = candidateLevel;
            }
        }

        if (selectedQuality < 0 || string.IsNullOrEmpty(selectedLevel))
        {
            return false;
        }

        level = selectedLevel;
        return true;
    }

    private static bool TryResolveMetadataLevelFromMediaType(string mediaType, out string level)
    {
        level = ODataMetadataMinimal;

        var parts = mediaType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var type = parts[0];
        if (!type.Equals("json", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals(ODataMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            level = ODataMetadataMinimal;
            return true;
        }

        foreach (var parameter in parts.Skip(1))
        {
            var kvp = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kvp.Length != 2)
            {
                continue;
            }

            if (!kvp[0].Equals("metadata", StringComparison.OrdinalIgnoreCase) &&
                !kvp[0].Equals("odata.metadata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = kvp[1].Trim().Trim('"').ToLowerInvariant();
            if (candidate is ODataMetadataMinimal or ODataMetadataFull or ODataMetadataNone)
            {
                level = candidate;
                return true;
            }
        }

        return false;
    }

    private static double ResolveQualityFactor(string mediaType)
    {
        var parameters = mediaType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var parameter in parameters.Skip(1))
        {
            var kvp = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kvp.Length != 2 || !kvp[0].Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = kvp[1].Trim().Trim('"');
            if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var quality) &&
                quality is >= 0 and <= 1)
            {
                return quality;
            }

            return 1.0;
        }

        return 1.0;
    }

    private static string ResolveMetadataLevel(HttpRequest request, string? format)
    {
        if (TryResolveMetadataLevel(format, out var formatLevel) ||
            TryResolveMetadataLevelFromAccept(request.Headers.Accept.ToString(), out formatLevel))
        {
            return formatLevel;
        }

        return ODataMetadataMinimal;
    }

    private static bool IsUnsupportedMetadataLevel(string level)
        => string.Equals(level, ODataMetadataFull, StringComparison.OrdinalIgnoreCase);

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
