// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Validation;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Route parameter validation service that consolidates validation patterns
/// for extracting and validating route parameters across all endpoints.
/// Reduces duplication in route value extraction logic.
/// </summary>
public interface IRouteParameterValidator
{
    /// <summary>
    /// Validates and extracts service ID from route values with proper error handling.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Validation result with extracted service ID or error details</returns>
    ValidationResult<string> ValidateServiceId(HttpContext context);

    /// <summary>
    /// Validates and extracts layer ID from route values with proper error handling.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Validation result with extracted layer ID or error details</returns>
    ValidationResult<int> ValidateLayerId(HttpContext context);

    /// <summary>
    /// Validates and extracts collection ID from route values for OGC API Features.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Validation result with extracted collection ID or error details</returns>
    ValidationResult<string> ValidateCollectionId(HttpContext context);

    /// <summary>
    /// Validates and extracts feature ID from route values.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Validation result with extracted feature ID or error details</returns>
    ValidationResult<string> ValidateFeatureId(HttpContext context);

    /// <summary>
    /// Validates and extracts relationship ID from route values.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Validation result with extracted relationship ID or error details</returns>
    ValidationResult<int> ValidateRelationshipId(HttpContext context);

    /// <summary>
    /// Validates HTTP method against allowed methods and writes appropriate error response if needed.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="allowedMethods">Set of allowed HTTP methods</param>
    /// <returns>Validation result indicating success or method not allowed</returns>
    ValidationResult ValidateHttpMethod(HttpContext context, ISet<string> allowedMethods);

    /// <summary>
    /// Validates content type for request body operations.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="allowedContentTypes">Set of allowed content types</param>
    /// <returns>Validation result with normalized content type or error details</returns>
    ValidationResult<string> ValidateContentType(HttpContext context, ISet<string> allowedContentTypes);
}

/// <summary>
/// Implementation of route parameter validation patterns.
/// </summary>
internal sealed class RouteParameterValidator : IRouteParameterValidator
{
    /// <inheritdoc/>
    public ValidationResult<string> ValidateServiceId(HttpContext context)
    {
        var serviceId = context.GetRouteValue("serviceId")?.ToString();

        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return ValidationResult<string>.Failure("Service ID is required");
        }

        // Basic service ID validation - alphanumeric, hyphens, underscores
        if (!IsValidIdentifier(serviceId))
        {
            return ValidationResult<string>.Failure("Service ID contains invalid characters");
        }

        return ValidationResult<string>.Success(serviceId);
    }

    /// <inheritdoc/>
    public ValidationResult<int> ValidateLayerId(HttpContext context)
    {
        if (!context.Request.RouteValues.TryGetValue("layerId", out var raw) || raw is null)
        {
            return ValidationResult<int>.Failure("Layer ID is required");
        }

        // Handle already parsed integer values (from typed route constraints)
        if (raw is int intValue)
        {
            return intValue < 0
                ? ValidationResult<int>.Failure("Layer ID must be non-negative")
                : ValidationResult<int>.Success(intValue);
        }

        // Parse string values
        var layerIdString = raw.ToString();
        if (!int.TryParse(layerIdString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return ValidationResult<int>.Failure("Layer ID must be a valid integer");
        }

        if (layerId < 0)
        {
            return ValidationResult<int>.Failure("Layer ID must be non-negative");
        }

        return ValidationResult<int>.Success(layerId);
    }

    /// <inheritdoc/>
    public ValidationResult<string> ValidateCollectionId(HttpContext context)
    {
        var collectionId = context.GetRouteValue("collectionId")?.ToString();

        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return ValidationResult<string>.Failure("Collection ID is required");
        }

        // Collection ID validation - more permissive than service ID to support various naming conventions
        if (collectionId.Length > 100)
        {
            return ValidationResult<string>.Failure("Collection ID is too long (maximum 100 characters)");
        }

        // Check for dangerous characters
        if (ContainsDangerousCharacters(collectionId))
        {
            return ValidationResult<string>.Failure("Collection ID contains invalid characters");
        }

        return ValidationResult<string>.Success(collectionId);
    }

    /// <inheritdoc/>
    public ValidationResult<string> ValidateFeatureId(HttpContext context)
    {
        var featureId = context.GetRouteValue("featureId")?.ToString();

        if (string.IsNullOrWhiteSpace(featureId))
        {
            return ValidationResult<string>.Failure("Feature ID is required");
        }

        // Feature ID validation - supports both integer and string identifiers
        if (featureId.Length > 100)
        {
            return ValidationResult<string>.Failure("Feature ID is too long (maximum 100 characters)");
        }

        // URL decode the feature ID
        var decodedFeatureId = Uri.UnescapeDataString(featureId);

        // Check for dangerous characters in the decoded value
        if (ContainsDangerousCharacters(decodedFeatureId))
        {
            return ValidationResult<string>.Failure("Feature ID contains invalid characters");
        }

        return ValidationResult<string>.Success(decodedFeatureId);
    }

    /// <inheritdoc/>
    public ValidationResult<int> ValidateRelationshipId(HttpContext context)
    {
        if (!context.Request.RouteValues.TryGetValue("relationshipId", out var raw) || raw is null)
        {
            return ValidationResult<int>.Failure("Relationship ID is required");
        }

        // Handle already parsed integer values
        if (raw is int intValue)
        {
            return intValue < 0
                ? ValidationResult<int>.Failure("Relationship ID must be non-negative")
                : ValidationResult<int>.Success(intValue);
        }

        // Parse string values
        var relationshipIdString = raw.ToString();
        if (!int.TryParse(relationshipIdString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relationshipId))
        {
            return ValidationResult<int>.Failure("Relationship ID must be a valid integer");
        }

        if (relationshipId < 0)
        {
            return ValidationResult<int>.Failure("Relationship ID must be non-negative");
        }

        return ValidationResult<int>.Success(relationshipId);
    }

    /// <inheritdoc/>
    public ValidationResult ValidateHttpMethod(HttpContext context, ISet<string> allowedMethods)
    {
        var method = context.Request.Method;

        if (!allowedMethods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase)))
        {
            // Set response status for method not allowed
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = string.Join(", ", allowedMethods);

            return ValidationResult.Failure($"Method '{method}' is not allowed. Allowed methods: {string.Join(", ", allowedMethods)}");
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult<string> ValidateContentType(HttpContext context, ISet<string> allowedContentTypes)
    {
        var contentType = context.Request.ContentType;

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ValidationResult<string>.Failure("Content-Type header is required");
        }

        // Extract media type without parameters (charset, etc.)
        var mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();

        if (!allowedContentTypes.Any(ct => string.Equals(ct, mediaType, StringComparison.OrdinalIgnoreCase)))
        {
            return ValidationResult<string>.Failure(
                $"Unsupported content type '{mediaType}'. Allowed types: {string.Join(", ", allowedContentTypes)}");
        }

        return ValidationResult<string>.Success(mediaType);
    }

    /// <summary>
    /// Validates identifier characters (alphanumeric, hyphens, underscores only).
    /// </summary>
    private static bool IsValidIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        // Must start with letter or underscore, contain only alphanumeric, hyphens, underscores
        return identifier.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_') &&
               (char.IsLetter(identifier[0]) || identifier[0] == '_');
    }

    /// <summary>
    /// Checks for characters that could be dangerous in identifiers.
    /// </summary>
    private static bool ContainsDangerousCharacters(string value)
    {
        // Check for path traversal, SQL injection, and XSS patterns
        var dangerousChars = new[] { '<', '>', '&', '"', '\'', '\0' };
        var dangerousPatterns = new[] { "..", "/*", "*/", "--", "script", "javascript:" };

        return value.IndexOfAny(dangerousChars) >= 0 ||
               dangerousPatterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
