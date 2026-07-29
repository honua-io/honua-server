// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Canonical implementation of <see cref="IProcessConditionalInputProbe"/> backed by
/// <see cref="ProcessPlanValidator"/> — the same validator the geoprocessing submit path
/// runs — so translation/migration tooling and submit-time execution agree on which input
/// combinations are admissible.
/// </summary>
internal sealed class ProcessConditionalInputProbe : IProcessConditionalInputProbe
{
    private const string MissingRequiredParameterCode = "MISSING_REQUIRED_PARAMETER";

    /// <summary>Upper bound on probed value assignments, keeping the cross-product cheap.</summary>
    private const int MaxProbeCombinations = 32;

    private readonly IProcessCatalog _catalog;

    /// <summary>
    /// Initializes the probe with the canonical process catalog.
    /// </summary>
    public ProcessConditionalInputProbe(IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindAdmissibilityViolations(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(suppliedParameterNames);

        var definition = _catalog.GetProcess(processId);
        if (definition is null)
        {
            return [];
        }

        // Presence is what the conditional rules test, so every supplied parameter is
        // probed with a non-blank placeholder. Declared defaults are preferred where
        // available so any value-shaped check sees a legal value; value-format violations
        // are filtered out below regardless, because the caller supplies names, not values.
        var defaults = definition.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.DefaultValue,
            StringComparer.OrdinalIgnoreCase);

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in suppliedParameterNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var canonicalName = definition.Parameters
                .FirstOrDefault(parameter =>
                    string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
                ?? name;

            inputs[canonicalName] = defaults.TryGetValue(canonicalName, out var declaredDefault)
                && !string.IsNullOrWhiteSpace(declaredDefault)
                    ? declaredDefault
                    : "1";
        }

        // A mapped parameter's value is caller-supplied, so assuming the catalog default
        // would reject mappings that are executable under a different value: transform.dedup
        // accepts 'keys' OR 'geometry=true', and probing the Flag's "false" default alone
        // would report a missing input the caller can simply avoid. Where a mapped parameter
        // has a finite domain, every assignment is probed and only violations that survive
        // all of them are real - no admissible value avoids those.
        var violations = FindUniversalViolations(definition, inputs, suppliedParameterNames);

        var results = new List<string>();
        foreach (var violation in violations)
        {
            // Missing-input failures are presence-based by construction.
            if (string.Equals(violation.Code, MissingRequiredParameterCode, StringComparison.Ordinal))
            {
                results.Add(violation.Message);
                continue;
            }

            // Anything else is only meaningful if it stems from the combination of supplied
            // parameters rather than from a value this probe fabricated. Withdraw each
            // other supplied parameter in turn: if the violation disappears, the inputs
            // conflict (for example the mutually-exclusive connectionName/connectionId
            // pair). If it survives every withdrawal, it is a complaint about the
            // substituted value and must not be reported.
            if (IsPresenceConflict(definition.ProcessId, inputs, violation))
            {
                results.Add(violation.Message);
            }
        }

        return results;
    }

    /// <summary>
    /// Validates every admissible assignment of the mapped parameters that have a finite
    /// value domain and returns only the violations common to all of them.
    /// </summary>
    private List<GeoprocessingValidationFailure> FindUniversalViolations(
        ProcessDefinition definition,
        Dictionary<string, string> baseInputs,
        IReadOnlyCollection<string> suppliedParameterNames)
    {
        var supplied = new HashSet<string>(suppliedParameterNames, StringComparer.OrdinalIgnoreCase);

        // Keep the cross-product bounded; parameters beyond the cap keep their base value.
        var variable = new List<(string Name, IReadOnlyList<string> Values)>();
        var combinationCount = 1;
        foreach (var parameter in definition.Parameters)
        {
            if (!supplied.Contains(parameter.Name))
            {
                continue;
            }

            var domain = DomainOf(parameter);
            if (domain is null || combinationCount * domain.Count > MaxProbeCombinations)
            {
                continue;
            }

            variable.Add((parameter.Name, domain));
            combinationCount *= domain.Count;
        }

        List<GeoprocessingValidationFailure>? universal = null;
        foreach (var assignment in Assignments(variable))
        {
            var candidate = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal);
            foreach (var (name, value) in assignment)
            {
                candidate[name] = value;
            }

            var found = Validate(definition.ProcessId, candidate);
            if (universal is null)
            {
                universal = found;
            }
            else
            {
                universal = [.. universal.Where(known => found.Any(other =>
                    string.Equals(other.Code, known.Code, StringComparison.Ordinal)
                    && string.Equals(other.FieldPath, known.FieldPath, StringComparison.Ordinal)))];
            }

            if (universal.Count == 0)
            {
                break;
            }
        }

        return universal ?? [];
    }

    /// <summary>
    /// Enumerates the cartesian product of the variable parameters' domains, yielding a
    /// single empty assignment when nothing varies.
    /// </summary>
    private static IEnumerable<IReadOnlyList<(string Name, string Value)>> Assignments(
        List<(string Name, IReadOnlyList<string> Values)> variable)
    {
        IEnumerable<IReadOnlyList<(string, string)>> product = [Array.Empty<(string, string)>()];

        foreach (var (name, values) in variable)
        {
            product = product.SelectMany(
                _ => values,
                (prefix, value) => (IReadOnlyList<(string, string)>)[.. prefix, (name, value)]);
        }

        return product;
    }

    /// <summary>
    /// Returns the finite set of values a parameter can take, or <c>null</c> when its domain
    /// is open (so no enumeration is possible).
    /// </summary>
    private static IReadOnlyList<string>? DomainOf(ProcessParameterSpec parameter)
    {
        if (parameter.AllowedValues is { Count: > 0 } allowed)
        {
            return allowed;
        }

        return parameter.ValueType == ProcessParameterValueType.Flag
            ? ["true", "false"]
            : null;
    }

    private bool IsPresenceConflict(
        string processId,
        Dictionary<string, string> inputs,
        GeoprocessingValidationFailure violation)
    {
        var attributedTo = ParameterNameOf(violation.FieldPath);

        foreach (var candidate in inputs.Keys)
        {
            // Withdrawing the parameter the violation is attributed to would clear a plain
            // value complaint too, so it proves nothing.
            if (string.Equals(candidate, attributedTo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var reduced = new Dictionary<string, string>(inputs, StringComparer.Ordinal);
            reduced.Remove(candidate);

            // Match on the message as well as the code/field: withdrawing one half of a
            // mutually-exclusive pair can substitute a *different* complaint about the same
            // parameter (dropping connectionName leaves a GUID-format violation on
            // connectionId, sharing its code and field path), which would otherwise read as
            // the original violation surviving.
            var stillPresent = Validate(processId, reduced)
                .Any(remaining => string.Equals(remaining.FieldPath, violation.FieldPath, StringComparison.Ordinal)
                    && string.Equals(remaining.Code, violation.Code, StringComparison.Ordinal)
                    && string.Equals(remaining.Message, violation.Message, StringComparison.Ordinal));

            if (!stillPresent)
            {
                return true;
            }
        }

        return false;
    }

    private List<GeoprocessingValidationFailure> Validate(
        string processId,
        Dictionary<string, string> inputs)
    {
        var plan = new AnalysisPlan
        {
            PlanId = "toolbox-translation-probe",
            IntentId = "toolbox-translation-probe",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "probe",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = inputs
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);
        return violations;
    }

    /// <summary>
    /// Extracts the parameter name from a <c>steps[id].inputs.name</c> field path.
    /// </summary>
    private static string? ParameterNameOf(string? fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath))
        {
            return null;
        }

        var separator = fieldPath.LastIndexOf('.');
        return separator >= 0 && separator < fieldPath.Length - 1
            ? fieldPath[(separator + 1)..]
            : null;
    }
}
