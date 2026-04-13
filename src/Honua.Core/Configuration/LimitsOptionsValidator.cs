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

        ValidateQueryLimits(options.Query, failures);
        ValidateTileLimits(options.Tiles, failures);
        ValidateEditLimits(options.Edits, failures);
        ValidateAttachmentLimits(options.Attachments, failures);
        ValidateConnectionLimits(options.Connections, failures);
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
