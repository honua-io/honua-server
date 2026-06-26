// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

/// <summary>
/// A single contingent-value violation produced while validating a feature's effective
/// attribute row against a resource's contingent-value field groups on the edit path.
/// </summary>
/// <param name="FieldGroupName">Name of the violated contingent-value field group.</param>
/// <param name="Message">Operator-facing message describing the invalid combination.</param>
public readonly record struct ContingentValueViolation(string FieldGroupName, string Message);

/// <summary>
/// Result of validating a feature's attributes against a resource's contingent-value groups.
/// </summary>
/// <param name="Violations">
/// Contingent-value violations. Empty when every restrictive group either does not apply or
/// is satisfied by an enumerated allowed combination.
/// </param>
public readonly record struct ContingentValueValidationResult(IReadOnlyList<ContingentValueViolation> Violations)
{
    /// <summary>True when no restrictive contingent-value group was violated.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>A reusable valid result with no violations.</summary>
    public static ContingentValueValidationResult Valid { get; } =
        new(Array.Empty<ContingentValueViolation>());
}

/// <summary>
/// Shared, provider-agnostic validator that enforces a resource's
/// <see cref="MetadataV2ContingentValueGroup"/> set against a feature's <em>effective</em>
/// attribute row (existing values merged with the changed values) on the edit path. Each
/// restrictive group permits only the value combinations it enumerates; a row that matches
/// none of the applicable combinations is rejected with a violation identifying the offending
/// group. Non-restrictive (advisory) groups never reject. Each enumerated field value supports
/// the <c>any</c> wildcard, an explicit <c>null</c>, a discrete coded value, or a numeric
/// range, layered over the per-field coded-value/range domains (#1878, #2133).
/// </summary>
public static class ContingentValueValidator
{
    /// <summary>
    /// Validates <paramref name="attributes"/> (the effective merged row) against the
    /// resource's restrictive contingent-value groups.
    /// </summary>
    /// <param name="resource">The resource whose contingent-value groups apply.</param>
    /// <param name="attributes">
    /// Case-insensitive effective feature attributes. For a partial update this must already
    /// be the existing row merged with the changed values so the full combination is checked.
    /// </param>
    /// <returns>The validation result carrying any violations.</returns>
    public static ContingentValueValidationResult Validate(
        MetadataV2Resource resource,
        IReadOnlyDictionary<string, object?> attributes)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);

        var groups = resource.ContingentValueGroups;
        if (groups.Count == 0)
        {
            return ContingentValueValidationResult.Valid;
        }

        var subtypeField = resource.Subtypes?.SubtypeField;
        object? subtypeValue = null;
        if (!string.IsNullOrEmpty(subtypeField))
        {
            attributes.TryGetValue(subtypeField, out subtypeValue);
        }

        List<ContingentValueViolation>? violations = null;

        foreach (var group in groups)
        {
            if (!group.Restrictive || group.Fields.Count == 0 || group.ContingentValues.Count == 0)
            {
                continue;
            }

            // Candidate combinations: those for the row's subtype (or subtype-agnostic).
            var matchedAny = false;
            var hadCandidate = false;

            foreach (var combination in group.ContingentValues)
            {
                if (combination.SubtypeCode is { } subtypeCode &&
                    !JsonElementMatches(subtypeValue, subtypeCode))
                {
                    continue;
                }

                hadCandidate = true;
                if (CombinationMatches(group, combination, attributes))
                {
                    matchedAny = true;
                    break;
                }
            }

            // A group with no combinations applicable to this subtype imposes no constraint;
            // only reject when at least one candidate existed and none matched.
            if (hadCandidate && !matchedAny)
            {
                violations ??= new List<ContingentValueViolation>();
                violations.Add(new ContingentValueViolation(
                    group.Name,
                    $"Attribute combination for fields ({string.Join(", ", group.Fields)}) violates contingent-value group '{group.Name}'."));
            }
        }

        return new ContingentValueValidationResult(
            (IReadOnlyList<ContingentValueViolation>?)violations ?? Array.Empty<ContingentValueViolation>());
    }

    private static bool CombinationMatches(
        MetadataV2ContingentValueGroup group,
        MetadataV2ContingentValue combination,
        IReadOnlyDictionary<string, object?> attributes)
    {
        foreach (var field in group.Fields)
        {
            attributes.TryGetValue(field, out var value);

            // A field not constrained by this combination accepts any value (wildcard).
            if (!combination.Values.TryGetValue(field, out var allowed))
            {
                continue;
            }

            if (!FieldValueMatches(value, allowed))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FieldValueMatches(object? value, MetadataV2ContingentFieldValue allowed)
    {
        switch (allowed.Type?.ToLowerInvariant())
        {
            case "any":
            case null:
                return true;
            case "null":
                return value is null;
            case "code":
                return allowed.Code is { } code && JsonElementMatches(value, code);
            case "range":
                return allowed.Range is { Count: 2 } range && InRange(value, range[0], range[1]);
            default:
                return false;
        }
    }

    private static bool JsonElementMatches(object? value, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return value is null;
            case JsonValueKind.Number when element.TryGetDouble(out var number):
                return TryToDouble(value, out var actual) && actual.Equals(number);
            case JsonValueKind.True:
                return value is bool b && b;
            case JsonValueKind.False:
                return value is bool b2 && !b2;
            case JsonValueKind.String:
                return string.Equals(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    element.GetString(),
                    StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private static bool InRange(object? value, JsonElement min, JsonElement max)
    {
        if (!TryToDouble(value, out var v) ||
            !min.TryGetDouble(out var lo) ||
            !max.TryGetDouble(out var hi))
        {
            return false;
        }

        return v >= lo && v <= hi;
    }

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case short s:
                result = s;
                return true;
            case byte bt:
                result = bt;
                return true;
            case string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
