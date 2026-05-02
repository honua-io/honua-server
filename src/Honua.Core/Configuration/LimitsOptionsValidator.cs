namespace Honua.Core.Configuration;

/// <summary>
/// Validates limits configuration beyond individual property annotations.
/// </summary>
public sealed class LimitsOptionsValidator : OptionsValidator<LimitsOptions>
{
    /// <inheritdoc />
    protected override void ValidateOptions(LimitsOptions options, List<string> failures)
    {
        ValidateDataAnnotations(options.Query, failures, "Query");
        ValidateDataAnnotations(options.Geometry, failures, "Geometry");
        ValidateDataAnnotations(options.Edits, failures, "Edits");
        ValidateDataAnnotations(options.Attachments, failures, "Attachments");
        ValidateDataAnnotations(options.Tiles, failures, "Tiles");
        ValidateDataAnnotations(options.Connections, failures, "Connections");
        ValidateDataAnnotations(options.Imports, failures, "Imports");
        ValidateDataAnnotations(options.Analytics, failures, "Analytics");
        ValidateDataAnnotations(options.Elevation, failures, "Elevation");

        ValidateQueryLimits(options.Query, failures);
        ValidateTileLimits(options.Tiles, failures);
        ValidateEditLimits(options.Edits, failures);
        ValidateAttachmentLimits(options.Attachments, failures);
        ValidateConnectionLimits(options.Connections, failures);
        ValidateElevationLimits(options.Elevation, failures);
    }

    internal static void ValidateElevationLimits(ElevationLimits limits, List<string> failures)
    {
        ValidateLogicalOrder(
            limits.DefaultSampleCount,
            limits.MaxSampleCount,
            "Elevation.DefaultSampleCount",
            "Elevation.MaxSampleCount",
            failures);

        ValidateLogicalOrder(
            limits.MinIntervalMeters,
            limits.MaxIntervalMeters,
            "Elevation.MinIntervalMeters",
            "Elevation.MaxIntervalMeters",
            failures);
    }

    internal static void ValidateQueryLimits(QueryLimits limits, List<string> failures)
    {
        ValidateLogicalOrder(
            limits.DefaultRecordCount,
            limits.MaxRecordCount,
            "Query.DefaultRecordCount",
            "Query.MaxRecordCount",
            failures);

        if (limits.QueryTimeout < TimeSpan.FromSeconds(5) || limits.QueryTimeout > TimeSpan.FromMinutes(2))
        {
            failures.Add("Query.QueryTimeout must be between 5 seconds and 2 minutes.");
        }
    }

    internal static void ValidateTileLimits(TileLimits limits, List<string> failures)
    {
        ValidateLogicalOrder(
            limits.MinTileZoom,
            limits.MaxTileZoom,
            "Tiles.MinTileZoom",
            "Tiles.MaxTileZoom",
            failures);
    }

    internal static void ValidateEditLimits(EditLimits limits, List<string> failures)
    {
        ValidateLogicalOrder(
            limits.MaxFeaturesPerEdit,
            limits.MaxEditsPerTransaction,
            "Edits.MaxFeaturesPerEdit",
            "Edits.MaxEditsPerTransaction",
            failures);
    }

    internal static void ValidateAttachmentLimits(AttachmentLimits limits, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(limits.AllowedMimeTypes))
        {
            failures.Add("Attachments.AllowedMimeTypes cannot be empty.");
        }
        else
        {
            foreach (var mimeType in limits.AllowedMimeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsValidMimeType(mimeType))
                {
                    failures.Add($"Attachments.AllowedMimeTypes contains invalid MIME type '{mimeType}'.");
                }
            }
        }

        try
        {
            checked
            {
                var totalPotentialSize = limits.MaxAttachmentSize * limits.MaxAttachmentsPerFeature;
                if (totalPotentialSize > limits.MaxTotalAttachmentSize)
                {
                    failures.Add("Attachments.MaxAttachmentSize multiplied by MaxAttachmentsPerFeature exceeds MaxTotalAttachmentSize.");
                }
            }
        }
        catch (OverflowException)
        {
            failures.Add("Attachments limits overflow when calculating total attachment size.");
        }
    }

    internal static void ValidateConnectionLimits(ConnectionLimits limits, List<string> failures)
    {
        if (limits.MaxConcurrentQueries > limits.MaxConnectionPoolSize)
        {
            failures.Add("Connections.MaxConcurrentQueries must be less than or equal to Connections.MaxConnectionPoolSize.");
        }

        if (limits.AdaptiveConcurrencyEnabled)
        {
            var ceiling = Math.Min(limits.MaxConcurrentQueries, limits.MaxConnectionPoolSize);
            if (limits.AdaptiveConcurrencyMinQueries > ceiling)
            {
                failures.Add("Connections.AdaptiveConcurrencyMinQueries must be less than or equal to the effective connection ceiling.");
            }

            if (limits.AdaptiveConcurrencyMaxQueries > 0 &&
                limits.AdaptiveConcurrencyMaxQueries > ceiling)
            {
                failures.Add("Connections.AdaptiveConcurrencyMaxQueries must be 0 or less than or equal to the effective connection ceiling.");
            }

            if (limits.AdaptiveConcurrencyMaxQueries > 0 &&
                limits.AdaptiveConcurrencyMaxQueries < limits.AdaptiveConcurrencyMinQueries)
            {
                failures.Add("Connections.AdaptiveConcurrencyMaxQueries must be 0 or greater than or equal to Connections.AdaptiveConcurrencyMinQueries.");
            }

            if (limits.AdaptiveConcurrencyInitialQueries > 0 &&
                limits.AdaptiveConcurrencyInitialQueries < limits.AdaptiveConcurrencyMinQueries)
            {
                failures.Add("Connections.AdaptiveConcurrencyInitialQueries must be 0 or greater than or equal to Connections.AdaptiveConcurrencyMinQueries.");
            }

            var adaptiveCeiling = limits.AdaptiveConcurrencyMaxQueries > 0
                ? limits.AdaptiveConcurrencyMaxQueries
                : ceiling;
            if (limits.AdaptiveConcurrencyInitialQueries > adaptiveCeiling)
            {
                failures.Add("Connections.AdaptiveConcurrencyInitialQueries must be less than or equal to the adaptive connection ceiling.");
            }
        }

        if (limits.RequestTimeout < TimeSpan.FromSeconds(10) || limits.RequestTimeout > TimeSpan.FromMinutes(10))
        {
            failures.Add("Connections.RequestTimeout must be between 10 seconds and 10 minutes.");
        }

        if (!string.IsNullOrWhiteSpace(limits.Multiplexing) &&
            !string.Equals(limits.Multiplexing, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(limits.Multiplexing, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(limits.Multiplexing, "false", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Connections.Multiplexing has an invalid value. Allowed values: auto, true, false.");
        }
    }

    internal static bool IsValidMimeType(string mimeType)
    {
        var parts = mimeType.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        return IsValidMimeToken(parts[0]) &&
               (parts[1] == "*" || IsValidMimeToken(parts[1]));
    }

    internal static bool IsValidMimeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsLetterOrDigit(character) &&
                character is not '!' and not '#' and not '$' and not '&' and not '-' and not '^' and not '_' and not '.'
                and not '+' and not '*')
            {
                return false;
            }
        }

        return true;
    }
}
