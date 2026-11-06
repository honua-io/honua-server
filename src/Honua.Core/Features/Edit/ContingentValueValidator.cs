// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

public readonly record struct ContingentValueViolation(string FieldGroupName, string Message);

public readonly record struct ContingentValueValidationResult(IReadOnlyList<ContingentValueViolation> Violations)
{
    public bool IsValid => Violations.Count == 0;

    public static ContingentValueValidationResult Valid { get; } =
        new(Array.Empty<ContingentValueViolation>());
}

public static class ContingentValueValidator
{
    // A partial update must already be merged with the stored row so the full combination is checked.
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
