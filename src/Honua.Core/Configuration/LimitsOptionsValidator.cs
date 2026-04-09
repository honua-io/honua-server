// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Validates LimitsOptions configuration to ensure consistent and safe limits.
/// Performs cross-property validation beyond individual DataAnnotations.
/// </summary>
public sealed class LimitsOptionsValidator : OptionsValidator<LimitsOptions>
{
    /// <summary>
    /// Validates the complete limits configuration, including cross-property rules.
    /// </summary>
    /// <param name="limits">The limits configuration to validate</param>
    /// <param name="failures">List to add validation errors to</param>
    protected override void ValidateOptions(LimitsOptions limits, List<string> failures)
    {
        // Validate individual objects using DataAnnotations
        ValidateDataAnnotations(limits.Query, failures, nameof(limits.Query));
        ValidateDataAnnotations(limits.Geometry, failures, nameof(limits.Geometry));
        ValidateDataAnnotations(limits.Edits, failures, nameof(limits.Edits));
        ValidateDataAnnotations(limits.Attachments, failures, nameof(limits.Attachments));
        ValidateDataAnnotations(limits.Tiles, failures, nameof(limits.Tiles));
        ValidateDataAnnotations(limits.Connections, failures, nameof(limits.Connections));
        ValidateDataAnnotations(limits.Imports, failures, nameof(limits.Imports));
        ValidateDataAnnotations(limits.Validation, failures, nameof(limits.Validation));

        // Cross-property validation rules
        ValidateQueryLimits(limits.Query, failures);
        ValidateTileLimits(limits.Tiles, failures);
        ValidateEditLimits(limits.Edits, failures);
        ValidateAttachmentLimits(limits.Attachments, failures);
        ValidateConnectionLimits(limits.Connections, failures);
    }


    /// <summary>
    /// Validates query limits for logical consistency.
    /// </summary>
    private static void ValidateQueryLimits(QueryLimits query, List<string> failures)
    {
        // DefaultRecordCount must not exceed MaxRecordCount
        ValidateLogicalOrder(query.DefaultRecordCount, query.MaxRecordCount, "Query.DefaultRecordCount", "Query.MaxRecordCount", failures);

        // Validate bbox area if specified
        if (query.MaxBboxAreaSqKm.HasValue)
        {
            ValidateRange(query.MaxBboxAreaSqKm.Value, 0.1, double.MaxValue, "Query.MaxBboxAreaSqKm", failures);
        }

        // Validate QueryTimeout range (5 seconds to 2 minutes)
        ValidateTimeSpan(query.QueryTimeout, TimeConstants.FiveSecondsTimeSpan, TimeConstants.TwoMinutesTimeSpan, "Query.QueryTimeout", failures);
    }

    /// <summary>
    /// Validates tile limits for logical consistency.
    /// </summary>
    private static void ValidateTileLimits(TileLimits tiles, List<string> failures)
    {
        // MinTileZoom must not exceed MaxTileZoom
        ValidateLogicalOrder(tiles.MinTileZoom, tiles.MaxTileZoom, "Tiles.MinTileZoom", "Tiles.MaxTileZoom", failures);

        // Validate zoom range bounds
        ValidateRange(tiles.MinTileZoom, 0, int.MaxValue, "Tiles.MinTileZoom", failures);
        ValidateRange(tiles.MaxTileZoom, 0, 24, "Tiles.MaxTileZoom", failures);

        // Validate TileTimeout range (1 second to 1 minute)
        ValidateTimeSpan(tiles.TileTimeout, TimeConstants.OneSecondTimeSpan, TimeConstants.OneMinuteTimeSpan, "Tiles.TileTimeout", failures);
    }

    /// <summary>
    /// Validates edit limits for logical consistency.
    /// </summary>
    private static void ValidateEditLimits(EditLimits edits, List<string> failures)
    {
        // MaxFeaturesPerEdit should be reasonable compared to MaxEditsPerTransaction
        ValidateLogicalOrder(edits.MaxFeaturesPerEdit, edits.MaxEditsPerTransaction, "Edits.MaxFeaturesPerEdit", "Edits.MaxEditsPerTransaction", failures);

        // Validate payload size is reasonable
        ValidateFileSize(edits.MaxPayloadSize, FileSizeConstants.OneMB, long.MaxValue, "Edits.MaxPayloadSize", failures);
    }

    /// <summary>
    /// Validates attachment limits for logical consistency.
    /// </summary>
    private static void ValidateAttachmentLimits(AttachmentLimits attachments, List<string> failures)
    {
        // MaxAttachmentSize * MaxAttachmentsPerFeature should not exceed MaxTotalAttachmentSize
        var maxTheoreticalTotal = attachments.MaxAttachmentSize * attachments.MaxAttachmentsPerFeature;
        if (maxTheoreticalTotal > attachments.MaxTotalAttachmentSize)
        {
            failures.Add($"Attachments: MaxAttachmentSize ({attachments.MaxAttachmentSize:N0}) * MaxAttachmentsPerFeature ({attachments.MaxAttachmentsPerFeature}) " +
                        $"exceeds MaxTotalAttachmentSize ({attachments.MaxTotalAttachmentSize:N0})");
        }

        // Validate MIME types format
        ValidateRequiredString(attachments.AllowedMimeTypes, "Attachments.AllowedMimeTypes", failures);

        if (!string.IsNullOrWhiteSpace(attachments.AllowedMimeTypes))
        {
            var mimeTypes = attachments.AllowedMimeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            ValidateCollectionCount(mimeTypes, 1, int.MaxValue, "Attachments.AllowedMimeTypes", failures);

            foreach (var mimeType in mimeTypes)
            {
                var trimmed = mimeType.Trim();
                if (string.IsNullOrEmpty(trimmed) || !IsValidMimeType(trimmed))
                {
                    failures.Add($"Attachments.AllowedMimeTypes contains invalid MIME type: '{mimeType}'");
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

    /// <summary>
    /// Valid values for <see cref="ConnectionLimits.Multiplexing"/>.
    /// Null or empty is treated as "unset" (the safe default is applied).
    /// </summary>
    private static readonly string[] AllowedMultiplexingValues = ["auto", "true", "false"];

    /// <summary>
    /// Validates connection limits for logical consistency.
    /// </summary>
    private static void ValidateConnectionLimits(ConnectionLimits connections, List<string> failures)
    {
        // Validate RequestTimeout range (10 seconds to 10 minutes)
        ValidateTimeSpan(connections.RequestTimeout, TimeConstants.TenSecondsTimeSpan, TimeConstants.TenMinutesTimeSpan, "Connections.RequestTimeout", failures);

        // Validate Multiplexing whitelist so a typo like "fasle" fails fast at
        // startup rather than silently enabling multiplexing at runtime.
        if (!string.IsNullOrWhiteSpace(connections.Multiplexing) &&
            !AllowedMultiplexingValues.Any(v => string.Equals(v, connections.Multiplexing, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(
                $"Connections.Multiplexing has invalid value '{connections.Multiplexing}'. " +
                "Allowed values: 'auto', 'true', 'false'.");
        }
    }
}
