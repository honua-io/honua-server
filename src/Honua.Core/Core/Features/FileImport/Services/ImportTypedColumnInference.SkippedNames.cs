// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.FileImport.Services;

// Source keys that cannot become typed columns stay in the properties document, so
// published features and exports do not show them. Report each one instead of
// hiding it silently. The importer reports 'id' and 'geometry' itself, with rename advice.
// Rejected columns are keys whose values fit no single column type, or that clash with another key by case.
internal sealed partial class ImportTypedColumnInference
{
    internal const int MaxReportedNames = 20;
    private const int MaxTrackedNames = 1_000;
    private const int MaxQuotedNameLength = 80;
    private const int MaxOverflowExamples = 5;

    private static readonly HashSet<string> SelfReportedNames = new(StringComparer.OrdinalIgnoreCase) { "id", "geometry" };

    private readonly List<string> _unsafeNames = [];
    private readonly HashSet<string> _unsafeSeen = new(StringComparer.Ordinal);
    private readonly List<string> _reservedNames = [];
    private readonly HashSet<string> _reservedSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _overflowNames = [];
    private readonly HashSet<string> _overflowSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CaseVariants> _caseVariants = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> SkippedNameWarnings()
    {
        var warnings = new List<string>(Math.Min(_unsafeNames.Count, MaxReportedNames) + _reservedNames.Count + 2);
        warnings.AddRange(_unsafeNames.Take(MaxReportedNames).Select(name =>
            $"Source property '{name}' was not published as a column because column names use ASCII letters, "
            + "digits and underscores only, start with a letter or underscore and have at most 63 characters. "
            + "Its values are kept in the stored properties document but are hidden in published features and exports. "
            + "Rename the property with those characters and re-import to publish its values."));

        if (_unsafeNames.Count > MaxReportedNames)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "More source properties whose names cannot be column names: {0}{1}. They are hidden in published features and exports.",
                _unsafeSeen.Count >= MaxTrackedNames ? "at least " : string.Empty,
                _unsafeNames.Count - MaxReportedNames));
        }

        warnings.AddRange(_reservedNames.Select(name =>
            $"Source property '{name}' conflicts with an importer-owned or system column name. "
            + "Its values are kept in the stored properties document but are hidden in published features and exports. "
            + "Rename the property to an unused name and re-import to publish its values."));

        warnings.AddRange(RejectedColumnWarnings());

        if (_overflowNames.Count > 0)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Source properties past the {2}-column limit that were not published as columns: {0}{1}, including {3}. "
                + "Their values are kept in the stored properties document but are hidden in published features and exports.",
                _overflowSeen.Count >= MaxTrackedNames ? "at least " : string.Empty,
                _overflowNames.Count,
                MaxColumns,
                string.Join(", ", _overflowNames.Take(MaxOverflowExamples).Select(name => $"'{name}'"))));
        }

        return warnings;
    }

    private void NoteUnpromotableName(string name)
    {
        if (!IsSafeIdentifier(name))
        {
            Track(name, _unsafeSeen, _unsafeNames);
        }
        else if (!SelfReportedNames.Contains(name))
        {
            Track(name, _reservedSeen, _reservedNames);
        }
    }

    private IEnumerable<string> RejectedColumnWarnings()
    {
        var rejected = _columns.Where(column => column.Value == Kind.Reject).Select(column => column.Key).ToList();
        var warnings = rejected.Take(MaxReportedNames).Select(name =>
            _caseVariants.TryGetValue(name, out var variants)
                ? $"Source properties {variants.Describe()} differ only in letter case, "
                    + "so none of them was published as a column. Their values are kept in the stored properties document "
                    + "but are hidden in published features and exports. Give each property a distinct name and re-import to publish them."
                : $"Source property '{name}' holds values that cannot be stored in a single typed column, such as nested objects, lists, "
                    + "binary data or out-of-range numbers, so it was not published as a column. "
                    + "Its values are kept in the stored properties document but are hidden in published features and exports.");
        return rejected.Count <= MaxReportedNames
            ? warnings
            : warnings.Append(string.Format(
                CultureInfo.InvariantCulture,
                "More source properties not published as columns: {0}. They are hidden in published features and exports.",
                rejected.Count - MaxReportedNames));
    }

    private void NoteCaseCollision(string name)
    {
        if (!_caseVariants.TryGetValue(name, out var variants))
        {
            variants = new CaseVariants();
            variants.Add(_exactNames.First(exact => exact.Equals(name, StringComparison.OrdinalIgnoreCase)));
            _caseVariants[name] = variants;
        }

        variants.Add(name);
    }

    private void NoteOverflowName(string name) => Track(name, _overflowSeen, _overflowNames);

    private static void Track(string name, HashSet<string> seen, List<string> quoted)
    {
        if (seen.Count < MaxTrackedNames && seen.Add(name))
        {
            quoted.Add(Quote(name));
        }
    }

    private static string Quote(string name)
    {
        if (name.Length <= MaxQuotedNameLength)
        {
            return name;
        }

        var length = char.IsHighSurrogate(name[MaxQuotedNameLength - 1]) ? MaxQuotedNameLength - 1 : MaxQuotedNameLength;
        return string.Concat(name.AsSpan(0, length), "…");
    }

    private sealed class CaseVariants
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _examples = [];
        private bool _more;

        public void Add(string name)
        {
            if (_seen.Contains(name) || _more)
            {
                return;
            }

            if (_seen.Count >= MaxOverflowExamples)
            {
                _more = true;
                return;
            }

            _seen.Add(name);
            _examples.Add(name);
        }

        public string Describe()
            => string.Join(" and ", _examples.Select(name => $"'{name}'")) + (_more ? " and other spellings" : string.Empty);
    }
}
