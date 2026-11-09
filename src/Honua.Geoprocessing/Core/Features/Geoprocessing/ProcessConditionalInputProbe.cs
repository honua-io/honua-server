// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal sealed class ProcessConditionalInputProbe : IProcessConditionalInputProbe
{
    private const string MissingRequiredParameterCode = "MISSING_REQUIRED_PARAMETER";

    private const string SyncOnlyProcessCode = "SYNC_ONLY_PROCESS";

    private const string WorkflowOnlyProcessCode = "WORKFLOW_ONLY_PROCESS";

    private const string ProcessUnavailableCode = "PROCESS_UNAVAILABLE";

    private const string UnclassifiedProcessCode = "UNCLASSIFIED_PROCESS";

    private const string AsyncExecutionUnsupportedCode = "ASYNC_EXECUTION_UNSUPPORTED";

    private const int MaxProbeCombinations = 32;

    private static readonly string[] DomainProbeValues =
    [
        "honuaProbeSentinel",
        "8f0a1c4e-6f1d-4a3a-9c2b-0d5e7f9a1b3c",
        "0,0,1,1"
    ];

    private readonly IProcessCatalog _catalog;

    public ProcessConditionalInputProbe(IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public IReadOnlyList<ProcessAdmissibilityViolation> FindAdmissibilityViolations(
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

        var supplied = new HashSet<string>(
            suppliedParameterNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        var inputs = BuildProbeInputs(definition, suppliedParameterNames);

        // A mapped parameter's value is caller-supplied, so assuming the catalog default
        // would reject mappings that are executable under a different value: transform.dedup
        // accepts 'keys' OR 'geometry=true', and probing the Flag's "false" default alone
        // would report a missing input the caller can simply avoid. Where a mapped parameter
        // has a finite domain, every assignment is probed and only violations that survive
        // all of them are real - no admissible value avoids those.
        var violations = FindUniversalViolations(definition, inputs, suppliedParameterNames);

        // Where that domain is NOT finite, BuildProbeInputs still pins the parameter to its
        // catalog default, which fabricates one branch out of a set this probe cannot
        // enumerate. Mapping analytics.cluster-managed's input/algorithm/k is executable when
        // the caller supplies algorithm=kmeans, but the fabricated 'dbscan' branch demands
        // eps/minPoints; reporting that would declare 'unsupported' a mapping that works as
        // written. Requirements only the fabricated branch imposes are therefore left to
        // FindUnverifiableConditionalParameters, which answers the same unenumerable-branch
        // case honestly with 'partially-translated'.
        var fabricatedDiscriminators = FabricatedDiscriminators(definition, supplied, inputs);

        var results = new List<ProcessAdmissibilityViolation>();

        foreach (var violation in violations)
        {
            // A non-Job catalog process is undispatchable whatever the parameters are, so it
            // is reported verbatim rather than run through the presence analysis below.
            if (violation.Code is SyncOnlyProcessCode
                or WorkflowOnlyProcessCode
                or ProcessUnavailableCode
                or UnclassifiedProcessCode
                or AsyncExecutionUnsupportedCode)
            {
                results.Add(new ProcessAdmissibilityViolation(
                    ProcessAdmissibilityViolationKind.NotJobExecutable, violation.Message));
                continue;
            }

            // Discount a violation ONLY when it is demonstrated to vary with the fabricated
            // discriminator's value. Discounting every non-Required parameter was too broad: it
            // also swallowed unconditional requirements, so surface.slope mapping `units` but
            // no source at all was downgraded to partially-translated instead of unsupported
            // (honua-server#2145 review).
            if (fabricatedDiscriminators.Count > 0
                && VariesWithFabricatedBranch(definition, inputs, fabricatedDiscriminators, violation))
            {
                continue;
            }

            // Missing-input failures are presence-based by construction.
            if (string.Equals(violation.Code, MissingRequiredParameterCode, StringComparison.Ordinal))
            {
                results.Add(new ProcessAdmissibilityViolation(
                    ProcessAdmissibilityViolationKind.Inputs, violation.Message));
                continue;
            }

            // Anything else is only meaningful if it stems from WHICH parameters are present
            // rather than from a value this probe fabricated.
            if (IsPresenceDependent(definition, inputs, violation))
            {
                results.Add(new ProcessAdmissibilityViolation(
                    ProcessAdmissibilityViolationKind.Inputs, violation.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<string> FindUnverifiableConditionalParameters(
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

        var supplied = new HashSet<string>(
            suppliedParameterNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        // Only a parameter that is neither supplied nor defaulted can go silently missing at
        // submit time, so those are the only candidates for an unprovable requirement.
        var candidates = definition.Parameters
            .Where(parameter => parameter.DefaultValue is null && !supplied.Contains(parameter.Name))
            .Select(parameter => parameter.Name)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        var baseInputs = BuildProbeInputs(definition, suppliedParameterNames);

        // When a supplied parameter's legal values cannot be enumerated, a caller can select a
        // branch this probe never visits — but that only makes a candidate unprovable if the
        // discriminator's value can actually change whether it is required. Returning EVERY
        // candidate downgraded mappings the submit path accepts: surface.slope mapping both
        // `source` and `units` had `layerId`/`rasterId` reported, even though `source` already
        // satisfies their exactly-one group and no value of `units` makes them required
        // (honua-server#2145 review). Report only the candidates whose requiredness some
        // substitution demonstrably turns on.
        if (!IsBranchSpaceEnumerable(definition, supplied, baseInputs))
        {
            // The pessimistic answer still stands for anything whose requiredness the
            // discriminator could turn on — probing sentinels cannot reach the real branches, so
            // their silence proves nothing. What CAN be proven is coverage: a candidate that a
            // supplied parameter already satisfies is not waiting on any branch. surface.slope
            // mapping `source` and `units` had layerId/rasterId reported even though `source`
            // satisfies their exactly-one group under every value of `units`
            // (honua-server#2145 review).
            return [.. candidates.Where(candidate =>
                !IsCoveredByASuppliedParameter(definition, supplied, baseInputs, candidate))];
        }

        // The branch space IS enumerable here, so every conditional requirement can be stated
        // exactly, branch by branch, by FindConditionalBranchRequirements. Nothing is left
        // unverifiable, and repeating those parameters here would report the same gap twice
        // under a weaker, conservative code (#3048).
        return [];
    }

    public IReadOnlyList<ProcessBranchRequirement> FindConditionalBranchRequirements(
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

        var supplied = new HashSet<string>(
            suppliedParameterNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        // Only a parameter that is neither supplied nor defaulted can go silently missing at
        // submit time, so those are the only candidates for a branch requirement.
        var candidates = new HashSet<string>(
            definition.Parameters
                .Where(parameter => parameter.DefaultValue is null && !supplied.Contains(parameter.Name))
                .Select(parameter => parameter.Name),
            StringComparer.OrdinalIgnoreCase);

        var baseInputs = BuildProbeInputs(definition, suppliedParameterNames);

        // An unenumerable (or budget-truncated) branch space cannot be answered exactly; that
        // case is the conservative one FindUnverifiableConditionalParameters owns.
        if (candidates.Count == 0 || !IsBranchSpaceEnumerable(definition, supplied, baseInputs))
        {
            return [];
        }

        // Walk the bounded cross-product of the supplied parameters' declared domains and
        // record, per assignment, which candidates the canonical validator reports missing.
        var perBranch = new List<(string Branch, Dictionary<string, string> Missing)>();
        foreach (var assignment in Assignments(VariableDomains(definition, supplied).Variable))
        {
            var inputs = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal);
            foreach (var (name, value) in assignment)
            {
                inputs[name] = value;
            }

            var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var failure in Validate(definition.ProcessId, inputs)
                .Where(failure => string.Equals(failure.Code, MissingRequiredParameterCode, StringComparison.Ordinal)))
            {
                var name = ParameterNameOf(failure.FieldPath);
                if (name is not null && candidates.Contains(name))
                {
                    missing[name] = failure.Message;
                }
            }

            perBranch.Add((DescribeBranch(assignment), missing));
        }

        // A requirement EVERY branch raises is unconditional, not branch-dependent:
        // FindAdmissibilityViolations already reports it, and repeating it here would present
        // an impossible mapping as a per-branch caveat.
        var universal = perBranch
            .Select(entry => (IEnumerable<string>)entry.Missing.Keys)
            .Aggregate((left, right) => left.Intersect(right, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. perBranch.SelectMany(entry => entry.Missing
                .Where(requirement => !universal.Contains(requirement.Key))
                .Select(requirement => new ProcessBranchRequirement(
                    entry.Branch, requirement.Key, requirement.Value)))
        ];
    }

    private static string DescribeBranch(IReadOnlyList<(string Name, string Value)> assignment)
        => assignment.Count == 0
            ? "(no discriminator)"
            : string.Join(", ", assignment.Select(pair => $"{pair.Name}={pair.Value}"));

    private bool IsBranchSpaceEnumerable(
        ProcessDefinition definition,
        HashSet<string> supplied,
        Dictionary<string, string> baseInputs)
        => !HasUnenumerableDiscriminator(definition, supplied, baseInputs)
            && !VariableDomains(definition, supplied).Truncated;

    private bool HasUnenumerableDiscriminator(
        ProcessDefinition definition,
        HashSet<string> supplied,
        Dictionary<string, string> baseInputs)
        => definition.Parameters.Any(parameter =>
            IsUnenumerableDiscriminator(definition, supplied, baseInputs, parameter));

    private bool IsUnenumerableDiscriminator(
        ProcessDefinition definition,
        HashSet<string> supplied,
        Dictionary<string, string> baseInputs,
        ProcessParameterSpec parameter)
        => supplied.Contains(parameter.Name)
            && parameter.ValueType == ProcessParameterValueType.Text
            && parameter.AllowedValues is not { Count: > 0 }
            && DomainProbeValues.All(probeValue =>
                IsRejectedAsForeignToken(definition.ProcessId, baseInputs, parameter.Name, probeValue));

    // A requirement that survives every substitution is real, and one that disappears is an artefact of the probe's sentinel.
    private bool VariesWithFabricatedBranch(
        ProcessDefinition definition,
        Dictionary<string, string> baseInputs,
        IReadOnlyList<string> discriminators,
        GeoprocessingValidationFailure violation)
    {
        foreach (var discriminator in discriminators)
        {
            // codeql[cs/linq/missed-select]: returns as soon as one probe value clears the violation
            foreach (var probeValue in DomainProbeValues)
            {
                var inputs = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal)
                {
                    [discriminator] = probeValue
                };

                if (!Validate(definition.ProcessId, inputs).Any(candidate => IsSameViolation(candidate, violation)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Silence under a sentinel the caller cannot send is not evidence that the parameter is optional.
    private bool IsCoveredByASuppliedParameter(
        ProcessDefinition definition,
        HashSet<string> supplied,
        Dictionary<string, string> baseInputs,
        string candidate)
    {
        // Whatever the current inputs already fail on is not evidence about the candidate.
        var baseline = FailureKeys(Validate(definition.ProcessId, baseInputs));

        foreach (var name in baseInputs.Keys.Where(supplied.Contains).ToArray())
        {
            var withheld = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal);
            withheld.Remove(name);

            // Withdrawing a supplied parameter must actually break something new, or it was not
            // carrying a requirement at all.
            var introduced = FailureKeys(Validate(definition.ProcessId, withheld)).Except(baseline).ToArray();
            if (introduced.Length == 0)
            {
                continue;
            }

            // The candidate covers that requirement exactly when supplying it INSTEAD repairs
            // what the withdrawal broke — which is what being alternatives in the same
            // exactly-one-of group means, whatever code the validator reports it under.
            var restored = new Dictionary<string, string>(withheld, StringComparer.Ordinal)
            {
                [candidate] = ProbeValueFor(definition, candidate)
            };

            if (!FailureKeys(Validate(definition.ProcessId, restored)).Intersect(introduced).Any())
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> FailureKeys(IEnumerable<GeoprocessingValidationFailure> failures)
        => [.. failures.Select(failure => $"{failure.Code}|{failure.FieldPath}")];

    private static string ProbeValueFor(ProcessDefinition definition, string parameterName)
    {
        var declaredDefault = definition.Parameters
            .FirstOrDefault(parameter =>
                string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase))?.DefaultValue;

        return string.IsNullOrWhiteSpace(declaredDefault) ? "1" : declaredDefault;
    }

    // The message is not part of the identity because rules interpolate the offending value.
    private static bool IsSameViolation(
        GeoprocessingValidationFailure candidate,
        GeoprocessingValidationFailure violation)
        => string.Equals(candidate.Code, violation.Code, StringComparison.Ordinal)
            && string.Equals(candidate.FieldPath, violation.FieldPath, StringComparison.Ordinal);

    private List<string> FabricatedDiscriminators(
        ProcessDefinition definition,
        HashSet<string> supplied,
        Dictionary<string, string> baseInputs)
        => [.. definition.Parameters
            .Where(parameter => IsUnenumerableDiscriminator(definition, supplied, baseInputs, parameter))
            .Select(parameter => parameter.Name)];

    private bool IsRejectedAsForeignToken(
        string processId,
        Dictionary<string, string> baseInputs,
        string parameterName,
        string probeValue)
    {
        var inputs = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal)
        {
            [parameterName] = probeValue
        };

        return Validate(processId, inputs).Any(failure =>
            string.Equals(ParameterNameOf(failure.FieldPath), parameterName, StringComparison.OrdinalIgnoreCase)
            && ProcessPlanValidator.IsClosedValueSetRejection(failure));
    }

    private static Dictionary<string, string> BuildProbeInputs(
        ProcessDefinition definition,
        IReadOnlyCollection<string> suppliedParameterNames)
    {
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

        return inputs;
    }

    private static (List<(string Name, IReadOnlyList<string> Values)> Variable, bool Truncated) VariableDomains(
        ProcessDefinition definition,
        HashSet<string> supplied)
    {
        var variable = new List<(string Name, IReadOnlyList<string> Values)>();
        var truncated = false;
        var combinationCount = 1;
        foreach (var parameter in definition.Parameters)
        {
            if (!supplied.Contains(parameter.Name))
            {
                continue;
            }

            var domain = DomainOf(parameter);
            if (domain is null)
            {
                continue;
            }

            if (combinationCount * domain.Count > MaxProbeCombinations)
            {
                truncated = true;
                continue;
            }

            variable.Add((parameter.Name, domain));
            combinationCount *= domain.Count;
        }

        return (variable, truncated);
    }

    private List<GeoprocessingValidationFailure> FindUniversalViolations(
        ProcessDefinition definition,
        Dictionary<string, string> baseInputs,
        IReadOnlyCollection<string> suppliedParameterNames)
    {
        var supplied = new HashSet<string>(suppliedParameterNames, StringComparer.OrdinalIgnoreCase);

        List<GeoprocessingValidationFailure>? universal = null;
        foreach (var assignment in Assignments(VariableDomains(definition, supplied).Variable))
        {
            var candidate = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal);
            foreach (var (name, value) in assignment)
            {
                candidate[name] = value;
            }

            var found = Validate(definition.ProcessId, candidate);
            universal = universal is null
                ? found
                : [.. universal.Where(known => found.Any(other =>
                    string.Equals(other.Code, known.Code, StringComparison.Ordinal)
                    && string.Equals(other.FieldPath, known.FieldPath, StringComparison.Ordinal)))];

            if (universal.Count == 0)
            {
                break;
            }
        }

        return universal ?? [];
    }

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

    private bool IsPresenceDependent(
        ProcessDefinition definition,
        Dictionary<string, string> inputs,
        GeoprocessingValidationFailure violation)
        => IsClearedByWithdrawal(definition.ProcessId, inputs, violation)
            || IsClearedByAddition(definition, inputs, violation);

    private bool IsClearedByWithdrawal(
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

    private bool IsClearedByAddition(
        ProcessDefinition definition,
        Dictionary<string, string> inputs,
        GeoprocessingValidationFailure violation)
    {
        foreach (var parameter in definition.Parameters)
        {
            if (inputs.ContainsKey(parameter.Name))
            {
                continue;
            }

            var augmented = new Dictionary<string, string>(inputs, StringComparer.Ordinal)
            {
                [parameter.Name] = ProbeValueFor(parameter)
            };

            var remaining = Validate(definition.ProcessId, augmented);

            // A probe value the validator itself rejects makes this run useless as evidence:
            // a semantic rule that stops at the first bad value can suppress the very check
            // under test, which would read as the violation clearing.
            if (remaining.Any(failure => string.Equals(
                    ParameterNameOf(failure.FieldPath), parameter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The field must come out completely clean, not merely free of this exact message.
            // Adding a parameter can only tighten the rules, so a *different* complaint left on
            // the same field (adding connectionName turns a GUID-format complaint about
            // connectionId into the mutually-exclusive one) means the field is still failing on
            // the value this probe substituted, which is not reportable.
            if (!remaining.Any(failure => string.Equals(
                    failure.FieldPath, violation.FieldPath, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ProbeValueFor(ProcessParameterSpec parameter)
    {
        if (!string.IsNullOrWhiteSpace(parameter.DefaultValue))
        {
            return parameter.DefaultValue;
        }

        if (parameter.AllowedValues is { Count: > 0 } allowed)
        {
            return allowed[0];
        }

        return parameter.ValueType == ProcessParameterValueType.Flag ? "true" : "1";
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

        // The submit path runs the direct-submit guards too, so a translation that only
        // satisfies ProcessPlanValidator could still be rejected, notably ProtocolOnly,
        // WorkflowOnly, and Unavailable process ids.
        var (directSubmitViolations, _) = DirectSubmitPlanValidator.Evaluate(plan, _catalog);
        violations.AddRange(directSubmitViolations.Where(candidate =>
            !violations.Any(existing =>
                string.Equals(existing.Code, candidate.Code, StringComparison.Ordinal)
                && string.Equals(existing.FieldPath, candidate.FieldPath, StringComparison.Ordinal))));

        return violations;
    }

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
