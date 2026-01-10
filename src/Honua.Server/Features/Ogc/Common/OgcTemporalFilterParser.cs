// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Ogc.Common;

internal static class OgcTemporalFilterParser
{
    public static bool TryParse(
        string? datetime,
        LayerDefinition layer,
        out TemporalFilter? temporalFilter,
        out string? errorMessage)
    {
        temporalFilter = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        var temporalField = layer.AttributeFields.FirstOrDefault(field =>
            field.Type is FieldType.DateTime or FieldType.Date);
        if (temporalField == null)
        {
            errorMessage = "No temporal field is available for filtering.";
            return false;
        }

        var parts = datetime.Split('/', StringSplitOptions.TrimEntries);
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (parts.Length == 1)
        {
            if (!TryParseDateTimeOffset(parts[0], out var instant))
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            start = instant;
            end = instant;
        }
        else if (parts.Length == 2)
        {
            if (!string.IsNullOrWhiteSpace(parts[0]) && parts[0] != "..")
            {
                if (!TryParseDateTimeOffset(parts[0], out var parsedStart))
                {
                    errorMessage = "Invalid datetime parameter.";
                    return false;
                }

                start = parsedStart;
            }

            if (!string.IsNullOrWhiteSpace(parts[1]) && parts[1] != "..")
            {
                if (!TryParseDateTimeOffset(parts[1], out var parsedEnd))
                {
                    errorMessage = "Invalid datetime parameter.";
                    return false;
                }

                end = parsedEnd;
            }
        }
        else
        {
            errorMessage = "Invalid datetime parameter.";
            return false;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = temporalField.Name,
            PropertyType = temporalField.Type == FieldType.Date ? TemporalPropertyType.Date : TemporalPropertyType.DateTime,
            Start = start,
            End = end
        };

        return true;
    }

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
}
