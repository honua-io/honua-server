// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Import.Services;

internal static partial class MigrationInventoryHelpers
{
    private const string RedactedValue = "[redacted]";

    private static readonly string[] SensitiveMetadataKeys =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "apikey",
        "api_key",
        "accesskey",
        "access_key",
        "sharedkey",
        "shared_key"
    ];

    public static MigrationCompatibilityAssessment Compatible(
        string reason,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? manualSteps = null,
        string? code = null)
        => CreateAssessment("compatible", reason, warnings, manualSteps, code);

    public static MigrationCompatibilityAssessment Partial(
        string reason,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? manualSteps = null,
        string? code = null)
        => CreateAssessment("partial", reason, warnings, manualSteps, code);

    public static MigrationCompatibilityAssessment Incompatible(
        string reason,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? manualSteps = null,
        string? code = null)
        => CreateAssessment("incompatible", reason, warnings, manualSteps, code);

    public static MigrationCompatibilityAssessment Aggregate(IEnumerable<MigrationCompatibilityAssessment> assessments, string emptyReason)
    {
        var materialized = assessments.ToArray();
        if (materialized.Length == 0)
        {
            return Compatible(emptyReason, code: ImportCompatibilityCodes.Empty);
        }

        var level = materialized.Any(a => IsIncompatible(a.Level))
            ? "incompatible"
            : materialized.Any(a => IsPartial(a.Level))
                ? "partial"
                : "compatible";

        var compatibleCount = materialized.Count(a => IsCompatible(a.Level));
        var partialCount = materialized.Count(a => IsPartial(a.Level));
        var incompatibleCount = materialized.Count(a => IsIncompatible(a.Level));

        var reason = $"Compatible: {compatibleCount}; partial: {partialCount}; incompatible: {incompatibleCount}.";

        return CreateAssessment(
            level,
            reason,
            materialized.SelectMany(a => a.Warnings),
            materialized.SelectMany(a => a.ManualSteps),
            code: null);
    }

    public static MigrationInventorySummary BuildSummary(
        MigrationInventoryContainer[] containers,
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[] styles,
        MigrationExternalDependency[] dependencies)
    {
        var allAssessments = containers.Select(c => c.Compatibility)
            .Concat(resources.Select(r => r.Compatibility))
            .Concat(styles.Select(s => s.Compatibility))
            .Concat(dependencies.Select(d => d.Compatibility))
            .ToArray();

        return new MigrationInventorySummary
        {
            ContainerCount = containers.Length,
            ResourceCount = resources.Length,
            StyleCount = styles.Length,
            ExternalDependencyCount = dependencies.Length,
            CompatibleCount = allAssessments.Count(a => IsCompatible(a.Level)),
            PartiallyCompatibleCount = allAssessments.Count(a => IsPartial(a.Level)),
            IncompatibleCount = allAssessments.Count(a => IsIncompatible(a.Level))
        };
    }

    public static MigrationInventoryCompleteness BuildCompleteness(
        string status,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? missingArtifacts = null)
        => new()
        {
            Status = status,
            Warnings = NormalizeStrings(warnings),
            MissingArtifacts = NormalizeStrings(missingArtifacts)
        };

    public static Dictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return [];
        }

        var sanitized = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
            {
                continue;
            }

            var stringValue = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            sanitized[key] = IsSensitiveKey(key)
                ? RedactedValue
                : SanitizeMetadataValue(stringValue);
        }

        return sanitized.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    public static async Task<MigrationSpatialReferenceInfo?> BuildSpatialReferenceAsync(
        ICrsRegistry crsRegistry,
        string role,
        string? sourceValue,
        CancellationToken cancellationToken,
        int? explicitSrid = null,
        string? explicitWkt = null,
        bool allowFallbackCrsUri = true)
    {
        var srid = explicitSrid ?? TryParseSrid(sourceValue) ?? TryParseSrid(explicitWkt);
        var definition = srid.HasValue
            ? await crsRegistry.ResolveBySridAsync(srid.Value, cancellationToken).ConfigureAwait(false)
            : null;

        var wkt = definition?.Wkt ?? explicitWkt ?? ExtractWkt(sourceValue);
        if (definition == null &&
            !srid.HasValue &&
            string.IsNullOrWhiteSpace(sourceValue) &&
            string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        var isGeographic = definition?.IsGeographic ?? InferIsGeographic(wkt, srid);
        var datum = ExtractDatum(wkt);
        var unit = ExtractUnit(wkt, isGeographic);

        return new MigrationSpatialReferenceInfo
        {
            Role = role,
            SourceValue = sourceValue,
            Srid = srid,
            CrsUri = definition?.Uri ?? (allowFallbackCrsUri && srid.HasValue ? $"http://www.opengis.net/def/crs/EPSG/0/{srid.Value}" : null),
            Datum = datum,
            Unit = unit,
            AxisOrder = definition.HasValue ? ToAxisOrderText(definition.Value.AxisOrder) : null,
            IsGeographic = isGeographic
        };
    }

    public static string? NormalizeExternalAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return address.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    public static string BuildExternalDependencyId(string prefix, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(address));
        return $"{prefix}:external:{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    public static MigrationCompatibilityAssessment FromGeoServerCompatibility(
        GeoServerResourceCompatibility? compatibility,
        string fallbackReason)
    {
        if (compatibility == null)
        {
            return Compatible(fallbackReason);
        }

        return compatibility.CompatibilityLevel switch
        {
            GeoServerCompatibilityLevel.FullyCompatible => Compatible(
                compatibility.Reason,
                compatibility.Warnings,
                compatibility.RequiredManualSteps,
                compatibility.Code),
            GeoServerCompatibilityLevel.PartiallyCompatible => Partial(
                compatibility.Reason,
                compatibility.Warnings,
                compatibility.RequiredManualSteps,
                compatibility.Code),
            _ => Incompatible(
                compatibility.Reason,
                compatibility.Warnings,
                compatibility.RequiredManualSteps,
                compatibility.Code)
        };
    }

    public static T[] OrderByStableKey<T>(IEnumerable<T> items, Func<T, string> keySelector)
        => items.OrderBy(keySelector, StringComparer.Ordinal).ToArray();

    public static string[] NormalizeStrings(IEnumerable<string>? values)
    {
        return values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static MigrationCompatibilityAssessment CreateAssessment(
        string level,
        string reason,
        IEnumerable<string>? warnings,
        IEnumerable<string>? manualSteps,
        string? code)
        => new()
        {
            Level = level,
            Code = string.IsNullOrWhiteSpace(code) ? null : code,
            Reason = reason,
            Warnings = NormalizeStrings(warnings),
            ManualSteps = NormalizeStrings(manualSteps)
        };

    private static bool IsCompatible(string? level)
        => string.Equals(level, "compatible", StringComparison.OrdinalIgnoreCase);

    private static bool IsPartial(string? level)
        => string.Equals(level, "partial", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompatible(string? level)
        => string.Equals(level, "incompatible", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveKey(string key)
        => SensitiveMetadataKeys.Any(value => key.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeMetadataValue(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return RedactedValue;
        }

        return NormalizeExternalAddress(value) ?? value;
    }

    private static string? ExtractWkt(string? sourceValue)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            return null;
        }

        return sourceValue.Contains('[', StringComparison.Ordinal) ? sourceValue : null;
    }

    private static int? TryParseSrid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = SridRegex().Match(value);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var srid))
        {
            return srid;
        }

        return int.TryParse(value, out srid) ? srid : null;
    }

    private static string? ExtractDatum(string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        var match = DatumRegex().Match(wkt);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractUnit(string? wkt, bool? isGeographic)
    {
        if (!string.IsNullOrWhiteSpace(wkt))
        {
            var lengthMatches = LengthUnitRegex().Matches(wkt);
            if (lengthMatches.Count > 0)
            {
                var matchIndex = isGeographic == false || ContainsProjectedCrsMarker(wkt)
                    ? lengthMatches.Count - 1
                    : 0;

                return lengthMatches[matchIndex].Groups[1].Value;
            }

            var angleMatch = AngleUnitRegex().Match(wkt);
            if (angleMatch.Success)
            {
                return angleMatch.Groups[1].Value;
            }
        }

        return isGeographic == true ? "degree" : null;
    }

    private static bool ContainsProjectedCrsMarker(string wkt)
        => wkt.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) ||
           wkt.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase) ||
           wkt.Contains("PROJECTEDCRS", StringComparison.OrdinalIgnoreCase);

    private static bool? InferIsGeographic(string? wkt, int? srid)
    {
        if (!string.IsNullOrWhiteSpace(wkt))
        {
            if (ContainsProjectedCrsMarker(wkt))
            {
                return false;
            }

            if (wkt.Contains("GEOGCS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEOGCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEODCRS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return srid switch
        {
            4326 or 4269 or 4267 => true,
            >= 4000 and <= 4999 => true,
            > 0 => false,
            _ => null
        };
    }

    private static string ToAxisOrderText(AxisOrder axisOrder)
        => axisOrder switch
        {
            AxisOrder.NorthEast => "north-east",
            _ => "east-north"
        };

    [GeneratedRegex(@"(?:EPSG:|AUTHORITY\[""EPSG"",""?)(\d{3,6})(?:""|])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SridRegex();

    [GeneratedRegex(@"(?:DATUM|GEODDATUM|ENSEMBLE)\s*\[\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatumRegex();

    [GeneratedRegex(@"(?:LENGTHUNIT|UNIT)\s*\[\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthUnitRegex();

    [GeneratedRegex(@"ANGLEUNIT\s*\[\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AngleUnitRegex();
}
