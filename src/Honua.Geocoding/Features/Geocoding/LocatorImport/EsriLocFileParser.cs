// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Geocoding.Features.Geocoding.LocatorImport;

/// <summary>
/// Parser for classic (text, <c>key = value</c>) Esri <c>.loc</c> locator definition files.
/// </summary>
/// <remarks>
/// Classic ArcGIS address locators persist their definition as an ANSI/UTF-8 text property file
/// (one <c>key = value</c> pair per line, <c>;</c>/<c>#</c> comments) carrying the locator style
/// ids, geocoding options (minimum match/candidate score, spelling sensitivity, offsets), and
/// reference-data bindings. ArcGIS Pro locators replaced this with an opaque binary payload
/// (<c>.loc</c> + <c>.lox</c> index + compressed data); those are detected and rejected with an
/// explicit error instead of being mis-parsed. Every key in the file is classified into the
/// translation report — supported keys are carried into <see cref="EsriLocatorDefinition"/>,
/// everything else is reported as unsupported/ignored rather than silently dropped (#2152).
/// </remarks>
internal static class EsriLocFileParser
{
    // Keys whose values feed EsriLocatorMatchSettings (classic geocoding options).
    private static readonly HashSet<string> _numericMatchKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "MinimumMatchScore",
        "MinimumCandidateScore",
        "SpellingSensitivity",
        "SideOffset",
        "EndOffset",
    };

    private static readonly HashSet<string> _booleanMatchKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "MatchIfScoresTie",
        "Interpolate",
    };

    /// <summary>
    /// Parses a classic text <c>.loc</c> definition. Throws <see cref="EsriLocatorImportException"/>
    /// for binary (ArcGIS Pro) payloads or content that is not a classic locator property file.
    /// </summary>
    /// <param name="content">Raw <c>.loc</c> file content.</param>
    /// <param name="locatorName">Locator name recorded on the parsed definition.</param>
    /// <param name="report">Translation report that receives one entry per source construct.</param>
    public static EsriLocatorDefinition Parse(
        ReadOnlySpan<byte> content,
        string locatorName,
        ICollection<LocatorTranslationEntry> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (content.IsEmpty)
        {
            throw new EsriLocatorImportException("The .loc locator definition file is empty.");
        }

        if (content.IndexOf((byte)0) >= 0)
        {
            throw new EsriLocatorImportException(
                "The .loc file contains a binary locator payload (ArcGIS Pro locator). Only classic " +
                "text .loc locator definitions are supported; rebuild the locator from its reference " +
                "data or export the reference data and import it directly.");
        }

        var text = Encoding.UTF8.GetString(content);

        string? version = null;
        string? styleId = null;
        string? category = null;
        var fields = new List<string>();
        double? minimumMatchScore = null, minimumCandidateScore = null, spellingSensitivity = null;
        double? sideOffset = null, endOffset = null;
        string? sideOffsetUnits = null;
        bool? matchIfScoresTie = null, interpolate = null;
        var parsedPairs = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                report.Add(new LocatorTranslationEntry(
                    Truncate(line), LocatorTranslationStatus.Unsupported,
                    "Line is not a recognized 'key = value' locator property."));
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                report.Add(new LocatorTranslationEntry(
                    Truncate(line), LocatorTranslationStatus.Unsupported,
                    "Line is not a recognized 'key = value' locator property."));
                continue;
            }

            parsedPairs++;

            if (_numericMatchKeys.Contains(key))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    switch (key.ToUpperInvariant())
                    {
                        case "MINIMUMMATCHSCORE": minimumMatchScore = number; break;
                        case "MINIMUMCANDIDATESCORE": minimumCandidateScore = number; break;
                        case "SPELLINGSENSITIVITY": spellingSensitivity = number; break;
                        case "SIDEOFFSET": sideOffset = number; break;
                        case "ENDOFFSET": endOffset = number; break;
                    }

                    report.Add(new LocatorTranslationEntry(
                        key, LocatorTranslationStatus.Supported,
                        "Match setting recorded from the source locator."));
                }
                else
                {
                    report.Add(new LocatorTranslationEntry(
                        key, LocatorTranslationStatus.Unsupported,
                        $"Value '{Truncate(value)}' is not a valid number."));
                }

                continue;
            }

            if (_booleanMatchKeys.Contains(key))
            {
                var parsed = ParseBoolean(value);
                if (parsed is null)
                {
                    report.Add(new LocatorTranslationEntry(
                        key, LocatorTranslationStatus.Unsupported,
                        $"Value '{Truncate(value)}' is not a valid boolean."));
                    continue;
                }

                if (key.Equals("MatchIfScoresTie", StringComparison.OrdinalIgnoreCase))
                {
                    matchIfScoresTie = parsed;
                }
                else
                {
                    interpolate = parsed;
                }

                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Supported,
                    "Match setting recorded from the source locator."));
                continue;
            }

            if (key.Equals("SideOffsetUnits", StringComparison.OrdinalIgnoreCase))
            {
                sideOffsetUnits = value;
                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Supported,
                    "Match setting recorded from the source locator."));
                continue;
            }

            if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                version = value;
                report.Add(new LocatorTranslationEntry(key, LocatorTranslationStatus.Supported));
                continue;
            }

            if (key.Equals("CLSID", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("UICLSID", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("CLSID", StringComparison.OrdinalIgnoreCase))
                {
                    styleId = value;
                }

                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Supported,
                    "Locator style identifier recorded."));
                continue;
            }

            if (key.Equals("Category", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Categories", StringComparison.OrdinalIgnoreCase))
            {
                category = value;
                report.Add(new LocatorTranslationEntry(key, LocatorTranslationStatus.Supported));
                continue;
            }

            if (key.Equals("Fields", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("Fields.", StringComparison.OrdinalIgnoreCase))
            {
                fields.Add($"{key} = {value}");
                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Supported,
                    "Input field metadata recorded."));
                continue;
            }

            if (key.Equals("CoordinateSystem", StringComparison.OrdinalIgnoreCase))
            {
                if (IsWgs84(value))
                {
                    report.Add(new LocatorTranslationEntry(key, LocatorTranslationStatus.Supported));
                }
                else
                {
                    report.Add(new LocatorTranslationEntry(
                        key, LocatorTranslationStatus.Unsupported,
                        "Only WGS84 geographic coordinates are supported; supply reference data " +
                        "coordinates as WGS84 longitude/latitude."));
                }

                continue;
            }

            if (key.StartsWith("ReferenceData", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("Data.", StringComparison.OrdinalIgnoreCase))
            {
                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Ignored,
                    "Reference data bindings are ignored; reference data is supplied directly in the import request."));
                continue;
            }

            if (key.StartsWith("AltName", StringComparison.OrdinalIgnoreCase))
            {
                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Unsupported,
                    "Alternate-name tables are not imported."));
                continue;
            }

            if (key.StartsWith("Composite", StringComparison.OrdinalIgnoreCase))
            {
                report.Add(new LocatorTranslationEntry(
                    key, LocatorTranslationStatus.Unsupported,
                    "Composite locators are not supported; import each participant locator separately."));
                continue;
            }

            report.Add(new LocatorTranslationEntry(
                key, LocatorTranslationStatus.Unsupported,
                "Locator property has no equivalent in the local geocoder and was not applied."));
        }

        if (parsedPairs == 0)
        {
            throw new EsriLocatorImportException(
                "The .loc file does not contain any 'key = value' locator properties and is not a " +
                "classic text locator definition.");
        }

        return new EsriLocatorDefinition
        {
            Name = locatorName,
            Version = version,
            StyleId = styleId,
            Category = category,
            Fields = fields,
            MatchSettings = new EsriLocatorMatchSettings
            {
                MinimumMatchScore = minimumMatchScore,
                MinimumCandidateScore = minimumCandidateScore,
                SpellingSensitivity = spellingSensitivity,
                SideOffset = sideOffset,
                SideOffsetUnits = sideOffsetUnits,
                EndOffset = endOffset,
                MatchIfScoresTie = matchIfScoresTie,
                Interpolate = interpolate,
            },
        };
    }

    private static bool? ParseBoolean(string value) => value.ToUpperInvariant() switch
    {
        "TRUE" or "YES" or "1" => true,
        "FALSE" or "NO" or "0" => false,
        _ => null,
    };

    private static bool IsWgs84(string coordinateSystem)
        => coordinateSystem.Contains("GCS_WGS_1984", StringComparison.OrdinalIgnoreCase)
            || coordinateSystem.Contains("WGS84", StringComparison.OrdinalIgnoreCase)
            || coordinateSystem.Contains("WGS 84", StringComparison.OrdinalIgnoreCase)
            || coordinateSystem.Contains("4326", StringComparison.Ordinal);

    private static string Truncate(string value)
        => value.Length <= 80 ? value : value[..77] + "...";
}
