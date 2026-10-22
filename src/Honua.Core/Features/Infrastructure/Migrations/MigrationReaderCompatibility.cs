// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Migrations;

/// <summary>
/// Classification values a forward-compatibility declaration can carry (#6477).
/// </summary>
public static class MigrationReaderCompatibilityClassification
{
    /// <summary>The script is additive and declared safe for older readers.</summary>
    public const string Expand = "expand";

    /// <summary>The shared classifier found a contract-phase (narrowing) change.</summary>
    public const string Contract = "contract";

    /// <summary>
    /// The author opted the script out of forward compatibility (<c>-- honua:reader-incompatible</c>),
    /// or a reader-compatibility annotation is malformed.
    /// </summary>
    public const string ReaderIncompatible = "reader-incompatible";
}

/// <summary>
/// A forward-compatibility declaration recorded beside an applied migration journal entry by the
/// server that applied it. An older reader accepts a journal entry it does not ship only after it
/// re-derives this declaration from <see cref="ScriptText"/> itself (see
/// <see cref="MigrationReaderCompatibility.Verify"/>); the declaring server is never trusted blindly.
/// </summary>
public sealed record MigrationReaderCompatibilityDeclaration
{
    /// <summary>The only declaration format this reader understands.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The exact journal identity (<c>public.schema_versions.scriptname</c>).</summary>
    public required string ScriptName { get; init; }

    /// <summary>Declaration format; readers refuse formats they do not implement.</summary>
    public required int DeclarationFormat { get; init; }

    /// <summary>One of the <see cref="MigrationReaderCompatibilityClassification"/> values.</summary>
    public required string Classification { get; init; }

    /// <summary>Uppercase SHA-256 over the UTF-8, LF-normalized <see cref="ScriptText"/>.</summary>
    public required string ScriptSha256 { get; init; }

    /// <summary>
    /// The oldest reader schema number the script is declared safe for. Zero unless the script raises
    /// it with <c>-- honua:reader-compatible-from schema=&lt;N&gt;</c>.
    /// </summary>
    public required int MinReaderSchema { get; init; }

    /// <summary>The LF-normalized script text that was applied, kept so a reader can re-classify it.</summary>
    public required string ScriptText { get; init; }
}

/// <summary>Outcome of an older reader re-verifying a recorded declaration.</summary>
/// <param name="Accepted">Whether the reader may serve with this journal entry applied.</param>
/// <param name="RefusalReason">Why the declaration was refused; <see langword="null"/> when accepted.</param>
public sealed record MigrationReaderCompatibilityVerdict(bool Accepted, string? RefusalReason)
{
    internal static MigrationReaderCompatibilityVerdict Accept { get; } = new(true, null);

    internal static MigrationReaderCompatibilityVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Forward-compatibility rule for image rollback across additive migrations (#6477). The server that
/// applies a migration records a <see cref="MigrationReaderCompatibilityDeclaration"/> for it. A later
/// rollback image that does not ship the script accepts its journal entry only when the declaration is
/// expand-only, its minimum reader schema is at most the reader's own schema number, and the reader's
/// own classifier, applied to the recorded and hash-bound script text, reaches the same declaration.
/// Contract-phase scripts, scripts carrying <c>-- honua:reader-incompatible</c>, and journal entries with
/// no declaration keep the <c>schema_reader_incompatible</c> refusal.
/// </summary>
public static partial class MigrationReaderCompatibility
{
    /// <summary>Opt-out an additive-looking script uses when an older reader would still break on it.</summary>
    public const string ReaderIncompatibleMarkerConvention =
        "-- honua:reader-incompatible reason=<what an older reader would violate>";

    /// <summary>Raises the oldest reader schema an additive script is declared safe for.</summary>
    public const string ReaderCompatibleFromMarkerConvention =
        "-- honua:reader-compatible-from schema=<oldest compatible reader schema number>";

    /// <summary>
    /// Builds the declaration the applying server records for <paramref name="scriptName"/>.
    /// </summary>
    /// <param name="scriptName">The exact journal identity.</param>
    /// <param name="sql">The script contents as discovered by the migration engine.</param>
    /// <returns>The declaration derived from the script text alone.</returns>
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

    /// <summary>
    /// Re-verifies a recorded declaration for a journal entry this reader does not ship.
    /// </summary>
    /// <param name="recorded">The declaration read from the database.</param>
    /// <param name="journalScriptName">The unknown journal entry being qualified.</param>
    /// <param name="readerSchema">This reader's own schema number (its highest migration number).</param>
    /// <returns>An accepting verdict only when every condition holds.</returns>
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

    /// <summary>
    /// The schema number of a reader that ships <paramref name="scriptNames"/>: the highest numbered
    /// migration it contains, or zero when none is numbered.
    /// </summary>
    /// <param name="scriptNames">The reader's own migration identities.</param>
    /// <returns>The reader schema number.</returns>
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

    /// <summary>The migration number in a script identity such as <c>Honua.Server.Migrations.135_X.sql</c>.</summary>
    /// <param name="scriptName">The script identity.</param>
    /// <returns>The number, or <see langword="null"/> when the name is not numbered.</returns>
    public static int? ReadSchemaNumber(string scriptName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        var match = MigrationNumber().Match(scriptName);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>Uppercase SHA-256 over UTF-8 text with LF line endings (the ledger's hash rule).</summary>
    /// <param name="sql">The script text.</param>
    /// <returns>The uppercase hexadecimal digest.</returns>
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
