// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Validates LimitsOptions configuration to ensure consistent and safe limits.
/// Performs cross-property validation beyond individual DataAnnotations.
/// </summary>
public static class LimitsOptionsValidator
{
    /// <summary>
    /// Validates the complete limits configuration, including cross-property rules.
    /// </summary>
    /// <param name="limits">The limits configuration to validate</param>
    /// <returns>List of validation errors, empty if valid</returns>
    public static List<string> Validate(LimitsOptions limits)
    {
        var errors = new List<string>();

        // Validate individual objects using DataAnnotations
        ValidateDataAnnotations(limits, errors, nameof(LimitsOptions));
        ValidateDataAnnotations(limits.Query, errors, nameof(limits.Query));
        ValidateDataAnnotations(limits.Geometry, errors, nameof(limits.Geometry));
        ValidateDataAnnotations(limits.Edits, errors, nameof(limits.Edits));
        ValidateDataAnnotations(limits.Attachments, errors, nameof(limits.Attachments));
        ValidateDataAnnotations(limits.Tiles, errors, nameof(limits.Tiles));
        ValidateDataAnnotations(limits.Connections, errors, nameof(limits.Connections));

        // Cross-property validation rules
        ValidateQueryLimits(limits.Query, errors);
        ValidateTileLimits(limits.Tiles, errors);
        ValidateEditLimits(limits.Edits, errors);
        ValidateAttachmentLimits(limits.Attachments, errors);

        return errors;
    }

    /// <summary>
    /// Validates an object using its DataAnnotations attributes.
    /// </summary>
    private static void ValidateDataAnnotations(object obj, List<string> errors, string propertyPath)
    {
        var context = new ValidationContext(obj);
        var results = new List<ValidationResult>();

        if (!Validator.TryValidateObject(obj, context, results, true))
        {
            foreach (var result in results)
            {
                var memberName = result.MemberNames.FirstOrDefault() ?? "Unknown";
                errors.Add($"{propertyPath}.{memberName}: {result.ErrorMessage}");
            }
        }
    }

    /// <summary>
    /// Validates query limits for logical consistency.
    /// </summary>
    private static void ValidateQueryLimits(QueryLimits query, List<string> errors)
    {
        // DefaultRecordCount must not exceed MaxRecordCount
        if (query.DefaultRecordCount > query.MaxRecordCount)
        {
            errors.Add($"Query.DefaultRecordCount ({query.DefaultRecordCount}) must not exceed MaxRecordCount ({query.MaxRecordCount})");
        }

        // Validate bbox area if specified
        if (query.MaxBboxAreaSqKm.HasValue && query.MaxBboxAreaSqKm.Value <= 0)
        {
            errors.Add("Query.MaxBboxAreaSqKm must be positive when specified");
        }
    }

    /// <summary>
    /// Validates tile limits for logical consistency.
    /// </summary>
    private static void ValidateTileLimits(TileLimits tiles, List<string> errors)
    {
        // MinTileZoom must not exceed MaxTileZoom
        if (tiles.MinTileZoom > tiles.MaxTileZoom)
        {
            errors.Add($"Tiles.MinTileZoom ({tiles.MinTileZoom}) must not exceed MaxTileZoom ({tiles.MaxTileZoom})");
        }

        // Validate zoom range bounds
        if (tiles.MinTileZoom < 0)
        {
            errors.Add("Tiles.MinTileZoom must be non-negative");
        }

        if (tiles.MaxTileZoom > 24)
        {
            errors.Add("Tiles.MaxTileZoom must not exceed 24 (maximum supported zoom level)");
        }
    }

    /// <summary>
    /// Validates edit limits for logical consistency.
    /// </summary>
    private static void ValidateEditLimits(EditLimits edits, List<string> errors)
    {
        // MaxFeaturesPerEdit should be reasonable compared to MaxEditsPerTransaction
        if (edits.MaxFeaturesPerEdit > edits.MaxEditsPerTransaction)
        {
            errors.Add($"Edits.MaxFeaturesPerEdit ({edits.MaxFeaturesPerEdit}) should not exceed MaxEditsPerTransaction ({edits.MaxEditsPerTransaction})");
        }

        // Validate payload size is reasonable
        if (edits.MaxPayloadSize < 1048576) // 1MB minimum
        {
            errors.Add("Edits.MaxPayloadSize must be at least 1MB");
        }
    }

    /// <summary>
    /// Validates attachment limits for logical consistency.
    /// </summary>
    private static void ValidateAttachmentLimits(AttachmentLimits attachments, List<string> errors)
    {
        // MaxAttachmentSize * MaxAttachmentsPerFeature should not exceed MaxTotalAttachmentSize
        var maxTheoreticalTotal = attachments.MaxAttachmentSize * attachments.MaxAttachmentsPerFeature;
        if (maxTheoreticalTotal > attachments.MaxTotalAttachmentSize)
        {
            errors.Add($"Attachments: MaxAttachmentSize ({attachments.MaxAttachmentSize:N0}) * MaxAttachmentsPerFeature ({attachments.MaxAttachmentsPerFeature}) " +
                      $"exceeds MaxTotalAttachmentSize ({attachments.MaxTotalAttachmentSize:N0})");
        }

        // Validate MIME types format
        if (string.IsNullOrWhiteSpace(attachments.AllowedMimeTypes))
        {
            errors.Add("Attachments.AllowedMimeTypes cannot be empty");
        }
        else
        {
            var mimeTypes = attachments.AllowedMimeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (mimeTypes.Length == 0)
            {
                errors.Add("Attachments.AllowedMimeTypes must contain at least one MIME type");
            }

            foreach (var mimeType in mimeTypes)
            {
                var trimmed = mimeType.Trim();
                if (string.IsNullOrEmpty(trimmed) || !IsValidMimeType(trimmed))
                {
                    errors.Add($"Attachments.AllowedMimeTypes contains invalid MIME type: '{mimeType}'");
                }
            }
        }
    }

    /// <summary>
    /// Basic MIME type format validation.
    /// </summary>
    private static bool IsValidMimeType(string mimeType)
    {
        // Basic validation for type/subtype or type/* patterns
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        var parts = mimeType.Split('/');
        if (parts.Length != 2)
            return false;

        var type = parts[0].Trim();
        var subtype = parts[1].Trim();

        // Type must be non-empty and contain only valid characters
        if (string.IsNullOrEmpty(type) || !IsValidMimeToken(type))
            return false;

        // Subtype can be * for wildcards or valid token
        if (string.IsNullOrEmpty(subtype) || (subtype != "*" && !IsValidMimeToken(subtype)))
            return false;

        return true;
    }

    /// <summary>
    /// Validates MIME token characters (simplified RFC compliance).
    /// </summary>
    private static bool IsValidMimeToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        // Simplified validation - must start with letter and contain only alphanumeric, hyphens, and underscores
        return token.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_') && char.IsLetter(token[0]);
    }
}
