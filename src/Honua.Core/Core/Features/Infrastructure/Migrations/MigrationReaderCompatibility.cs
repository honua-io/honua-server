// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Migrations;

public static class MigrationReaderCompatibilityClassification
{
    public const string Expand = "expand";

    public const string Contract = "contract";

    public const string ReaderIncompatible = "reader-incompatible";
}

// An older reader accepts an unknown journal entry only after it re-derives the declaration itself; the declaring server is not trusted.
public sealed record MigrationReaderCompatibilityDeclaration
{
    public const int CurrentFormat = 1;

    public required string ScriptName { get; init; }

    // Readers refuse declaration formats they do not implement.
    public required int DeclarationFormat { get; init; }

    public required string Classification { get; init; }

    public required string ScriptSha256 { get; init; }

    public required int MinReaderSchema { get; init; }

    public required string ScriptText { get; init; }
}

public sealed record MigrationReaderCompatibilityVerdict(bool Accepted, string? RefusalReason)
{
    internal static MigrationReaderCompatibilityVerdict Accept { get; } = new(true, null);

    internal static MigrationReaderCompatibilityVerdict Refuse(string reason) => new(false, reason);
}

public static partial class MigrationReaderCompatibility
{
    public const string ReaderIncompatibleMarkerConvention =
        "-- honua:reader-incompatible reason=<what an older reader would violate>";

    public const string ReaderCompatibleFromMarkerConvention =
        "-- honua:reader-compatible-from schema=<oldest compatible reader schema number>";

    public static MigrationReaderCompatibilityDeclaration Declare(string scriptName, string sql)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        ArgumentNullException.ThrowIfNull(sql);

        var text = NormalizeLineEndings(sql);
        var (minReaderSchema, malformedFloor) = ReadMinReaderSchema(text);
        string classification;
        if (ReaderIncompatibleMarker().IsMatch(text) || malformedFloor)
        {
            classification = MigrationReaderCompatibilityClassification.ReaderIncompatible;
        }
        else if (MigrationSafetyClassifier.Classify(scriptName, text).IsBreaking)
        {
            classification = MigrationReaderCompatibilityClassification.Contract;
        }
        else
        {
            classification = MigrationReaderCompatibilityClassification.Expand;
        }

        return new MigrationReaderCompatibilityDeclaration
        {
            ScriptName = scriptName,
            DeclarationFormat = MigrationReaderCompatibilityDeclaration.CurrentFormat,
            Classification = classification,
            ScriptSha256 = ComputeScriptSha256(text),
            MinReaderSchema = minReaderSchema,
            ScriptText = text,
        };
    }

    public static MigrationReaderCompatibilityVerdict Verify(
        MigrationReaderCompatibilityDeclaration recorded,
        string journalScriptName,
        int readerSchema)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(journalScriptName);

        if (!string.Equals(recorded.ScriptName, journalScriptName, StringComparison.Ordinal))
        {
            return MigrationReaderCompatibilityVerdict.Refuse("the declaration names a different script");
        }

        if (recorded.DeclarationFormat != MigrationReaderCompatibilityDeclaration.CurrentFormat)
        {
            return MigrationReaderCompatibilityVerdict.Refuse(
                $"declaration format {recorded.DeclarationFormat.ToString(CultureInfo.InvariantCulture)} is not supported by this reader");
        }

        if (!string.Equals(recorded.Classification, MigrationReaderCompatibilityClassification.Expand, StringComparison.Ordinal))
        {
            return MigrationReaderCompatibilityVerdict.Refuse($"declared {recorded.Classification}, not expand");
        }

        if (recorded.MinReaderSchema > readerSchema)
        {
            return MigrationReaderCompatibilityVerdict.Refuse(
                $"declared compatible from reader schema {recorded.MinReaderSchema.ToString(CultureInfo.InvariantCulture)}; " +
                $"this reader is schema {readerSchema.ToString(CultureInfo.InvariantCulture)}");
        }

        if (recorded.ScriptText is null
            || !string.Equals(ComputeScriptSha256(recorded.ScriptText), recorded.ScriptSha256, StringComparison.Ordinal))
        {
            return MigrationReaderCompatibilityVerdict.Refuse("the recorded script text does not match its recorded hash");
        }

        // The reader's own classifier decides. A newer image cannot widen what this reader accepts.
        var own = Declare(journalScriptName, recorded.ScriptText);
        if (!string.Equals(own.Classification, MigrationReaderCompatibilityClassification.Expand, StringComparison.Ordinal))
        {
            return MigrationReaderCompatibilityVerdict.Refuse($"this reader classifies the recorded script as {own.Classification}");
        }

        if (own.MinReaderSchema != recorded.MinReaderSchema)
        {
            return MigrationReaderCompatibilityVerdict.Refuse(
                "the declared minimum reader schema disagrees with the recorded script's annotation");
        }

        return MigrationReaderCompatibilityVerdict.Accept;
    }

    public static int ReaderSchemaOf(IEnumerable<string> scriptNames)
    {
        ArgumentNullException.ThrowIfNull(scriptNames);
        return scriptNames
            .Select(ReadSchemaNumber)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .DefaultIfEmpty(0)
            .Max();
    }

    public static int? ReadSchemaNumber(string scriptName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        var match = MigrationNumber().Match(scriptName);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    public static string ComputeScriptSha256(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeLineEndings(sql))));
    }

    private static string NormalizeLineEndings(string sql) => sql.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static (int MinReaderSchema, bool Malformed) ReadMinReaderSchema(string text)
    {
        var floors = ReaderCompatibleFromMarker().Matches(text)
            .Select(marker => ParseFloor(marker.Value))
            .ToArray();

        // Fail safe: an annotation the reader cannot parse never widens acceptance.
        return (floors.Max() ?? 0, floors.Any(floor => floor is null));
    }

    private static int? ParseFloor(string marker)
    {
        var parsed = ReaderCompatibleFromValue().Match(marker);
        return parsed.Success
            && int.TryParse(parsed.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    // Any reader-incompatible marker forces refusal, with or without a reason; the reason is
    // required by review convention, not by the parser, so the opt-out can never fail open.
    [GeneratedRegex(@"^[ \t]*--[ \t]*honua:reader-incompatible\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReaderIncompatibleMarker();

    [GeneratedRegex(@"^[ \t]*--[ \t]*honua:reader-compatible-from\b[^\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReaderCompatibleFromMarker();

    [GeneratedRegex(@"^[ \t]*--[ \t]*honua:reader-compatible-from[ \t]+schema[ \t]*=[ \t]*(\d{1,9})[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReaderCompatibleFromValue();

    [GeneratedRegex(@"(?:^|[./\\])0*(\d+)_", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationNumber();
}
