// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;

namespace Honua.Geocoding.Features.Geocoding.ReferenceDataImport;

/// <summary>
/// Minimal streaming RFC 4180 CSV record reader for geocoder reference data uploads.
/// </summary>
/// <remarks>
/// Scoped to the reference data import path: the shared feature-oriented CSV reader lives in the
/// geometry satellite (<c>Honua.Geometry</c>, internal, <c>IFeature</c>-shaped), which the
/// geocoding satellite cannot reference under the module dependency policy, so this reader
/// handles the raw-record shape the reference loader needs (quoted fields, escaped quotes,
/// CRLF/LF, streaming — no full-file buffering).
/// </remarks>
internal static class GeocoderReferenceCsv
{
    /// <summary>
    /// Streams CSV records (including the header row) from <paramref name="reader"/>. Records that
    /// are completely empty are skipped.
    /// </summary>
    public static async IAsyncEnumerable<string[]> ReadRecordsAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var buffer = new char[8192];
        var field = new StringBuilder();
        var fields = new List<string>();
        var inQuotes = false;
        var quotedField = false;
        var pendingQuote = false;
        var previousWasCarriageReturn = false;
        var recordHasContent = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (c == '"')
                    {
                        // Escaped quote inside a quoted field.
                        field.Append('"');
                        continue;
                    }

                    // Closing quote: fall through and process c as an unquoted character.
                    inQuotes = false;
                }

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        pendingQuote = true;
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' when field.Length == 0 && !quotedField:
                        inQuotes = true;
                        quotedField = true;
                        recordHasContent = true;
                        break;

                    case ',':
                        fields.Add(field.ToString());
                        field.Clear();
                        quotedField = false;
                        recordHasContent = true;
                        break;

                    case '\r':
                        previousWasCarriageReturn = true;
                        if (TryCompleteRecord(fields, field, ref quotedField, ref recordHasContent, out var crRecord))
                        {
                            yield return crRecord;
                        }

                        break;

                    case '\n':
                        if (previousWasCarriageReturn)
                        {
                            previousWasCarriageReturn = false;
                            break;
                        }

                        if (TryCompleteRecord(fields, field, ref quotedField, ref recordHasContent, out var lfRecord))
                        {
                            yield return lfRecord;
                        }

                        break;

                    default:
                        field.Append(c);
                        recordHasContent = true;
                        break;
                }

                if (c != '\r')
                {
                    previousWasCarriageReturn = false;
                }
            }
        }

        if (inQuotes && !pendingQuote)
        {
            throw new GeocoderReferenceDataImportException(
                "The reference data CSV ends inside an unterminated quoted field.");
        }

        if (TryCompleteRecord(fields, field, ref quotedField, ref recordHasContent, out var finalRecord))
        {
            yield return finalRecord;
        }
    }

    private static bool TryCompleteRecord(
        List<string> fields,
        StringBuilder field,
        ref bool quotedField,
        ref bool recordHasContent,
        out string[] record)
    {
        if (!recordHasContent && fields.Count == 0 && field.Length == 0)
        {
            record = [];
            return false;
        }

        fields.Add(field.ToString());
        field.Clear();
        record = [.. fields];
        fields.Clear();
        quotedField = false;
        recordHasContent = false;
        return true;
    }
}
