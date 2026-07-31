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

    private const string SyncOnlyProcessCode = "SYNC_ONLY_PROCESS";

    /// <summary>Upper bound on probed value assignments, keeping the cross-product cheap.</summary>
    private const int MaxProbeCombinations = 32;

    /// <summary>
    /// Structurally different, individually legal-looking values used to tell a parameter the
    /// validator constrains to a finite token set it does not declare (an undeclared
    /// discriminator such as <c>algorithm</c> or <c>op</c>) from one it merely constrains by
    /// format. An undeclared token domain rejects all three as outside its allowed set; a
    /// format rule either accepts one of them (a GUID rule accepts the second, an identifier
    /// rule the first, a numeric-list rule the third) or rejects them for a reason other than
    /// set membership, and neither is reported — see <see cref="IsRejectedAsForeignToken"/>.
    /// </summary>
    private static readonly string[] DomainProbeValues =
    [
        "honuaProbeSentinel",
        "8f0a1c4e-6f1d-4a3a-9c2b-0d5e7f9a1b3c",
        "0,0,1,1"
    ];

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

        // Some processes are advertised for discoverability but have no working executor in
        // this build, so no parameter set makes them run. The submit path deliberately admits
        // them (the limitation surfaces as an explicit job failure), but certifying one here
        // would tell a migrating user a tool works when it can only fail.
        if (BuiltInProcessCatalog.AdvertisedButNotExecutableProcesses.TryGetValue(
                definition.ProcessId, out var notExecutableReason))
        {
            results.Add(new ProcessAdmissibilityViolation(
                ProcessAdmissibilityViolationKind.NotJobExecutable, notExecutableReason));
        }

        foreach (var violation in violations)
        {
            // A sync-only process is undispatchable whatever the parameters are, so it is
            // reported verbatim rather than run through the presence analysis below.
            if (string.Equals(violation.Code, SyncOnlyProcessCode, StringComparison.Ordinal))
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

    /// <inheritdoc />
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
        // branch this probe never visits, so nothing about the candidates is provable and the
        // pessimistic answer stands: over-claiming 'executable' is worse than over-reporting.
        if (HasUnenumerableDiscriminator(definition, supplied, baseInputs))
        {
            return candidates;
        }

        // The branch space IS enumerable here, so a candidate is unverifiable exactly when
        // some admissible assignment makes the canonical validator require it. A candidate no
        // assignment ever requires is unconditionally optional — the submit path accepts its
        // omission, and reporting it would misclassify an executable mapping.
        var branchDependent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in Assignments(VariableDomains(definition, supplied)))
        {
            var inputs = new Dictionary<string, string>(baseInputs, StringComparer.Ordinal);
            foreach (var (name, value) in assignment)
            {
                inputs[name] = value;
            }

            foreach (var failure in Validate(definition.ProcessId, inputs))
            {
                if (!string.Equals(failure.Code, MissingRequiredParameterCode, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = ParameterNameOf(failure.FieldPath);
                if (name is not null)
                {
                    branchDependent.Add(name);
                }
            }
        }

        return [.. candidates.Where(branchDependent.Contains)];
    }

    /// <summary>
    /// Returns true when some supplied parameter's value domain is a finite token set the
    /// canonical validator enforces but the catalog does not declare, so branch enumeration
    /// is impossible. Declared <c>AllowedValues</c> and boolean flags are enumerated exactly
    /// and never qualify; only free <c>Text</c> carries the undeclared token domains the
    /// per-process rules branch on (<c>algorithm</c>, <c>op</c>). Numeric parameters are
    /// range-constrained rather than token-constrained, and a numeric discriminator would
    /// have to declare <c>AllowedValues</c> to be enumerable at all.
    /// </summary>
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

    /// <summary>
    /// Returns true when <paramref name="violation"/> is DEMONSTRATED to depend on the value the
    /// probe fabricated for a discriminator, by re-validating with that discriminator moved to a
    /// different value and observing the violation disappear.
    /// </summary>
    /// <remarks>
    /// A requirement that survives every substitution holds in whatever branch the caller
    /// selects, so it is a real reason the mapping cannot execute and must be reported. Only a
    /// requirement that some other value removes is an artefact of the branch this probe
    /// happened to pin — that is the case
    /// <see cref="FindUnverifiableConditionalParameters"/> answers honestly as
    /// branch-unverifiable.
    /// <para>
    /// The substituted values are the same sentinels the discriminator test uses. They are
    /// rejected as foreign tokens by definition — that is what made the domain unenumerable —
    /// but the canonical validator still evaluates every other rule, so a conditional
    /// requirement keyed on the DEFAULT value is absent from that run while an unconditional one
    /// persists. That difference is the whole signal, and it does not require guessing a legal
    /// value from a domain the catalog never declared.
    /// </para>
    /// <para>
    /// Replaces a coarser rule that discounted any violation on a non-<c>Required</c> parameter.
    /// A member of an exactly-one-of source group is not declared <c>Required</c> either, so
    /// that rule silently swallowed <c>surface.slope</c>'s unconditional missing-source failure
    /// and certified a mapping that can never run (honua-server#2145 review).
    /// </para>
    /// </remarks>
    private bool VariesWithFabricatedBranch(
        ProcessDefinition definition,
        Dictionary<string, string> baseInputs,
        IReadOnlyList<string> discriminators,
        GeoprocessingValidationFailure violation)
    {
        foreach (var discriminator in discriminators)
        {
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

    /// <summary>
    /// Identity for comparing a violation across probe runs: the same rule on the same field.
    /// The message is deliberately not compared — several rules interpolate the offending value,
    /// which changes with every substitution.
    /// </summary>
    private static bool IsSameViolation(
        GeoprocessingValidationFailure candidate,
        GeoprocessingValidationFailure violation)
        => string.Equals(candidate.Code, violation.Code, StringComparison.Ordinal)
            && string.Equals(candidate.FieldPath, violation.FieldPath, StringComparison.Ordinal);

    /// <summary>
    /// The supplied parameters whose value the probe had to fabricate because their domain is
    /// an undeclared token set. Empty when the branch space is enumerable.
    /// </summary>
    private List<string> FabricatedDiscriminators(
        ProcessDefinition definition,
        HashSet<string> supplied,
        Dictionary<string, string> baseInputs)
        => [.. definition.Parameters
            .Where(parameter => IsUnenumerableDiscriminator(definition, supplied, baseInputs, parameter))
            .Select(parameter => parameter.Name)];

    /// <summary>
    /// Substitutes <paramref name="probeValue"/> for <paramref name="parameterName"/> and reports
    /// whether the canonical validator rejects it as outside a closed token set.
    /// <para>
    /// Only a token-set rejection counts. A structured-text parameter whose FORMAT the validator
    /// checks — <c>raster.map-algebra</c>'s <c>expression</c> rejects everything that is not an
    /// allow-listed band expression — also refuses all three sentinels, but it is not a
    /// discriminator: no per-process rule branches on its value, so treating it as one would
    /// return every optional omission as unverifiable and downgrade a mapping the submit path
    /// accepts.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Builds the probe's base input set: presence is what the conditional rules test, so every
    /// supplied parameter gets a non-blank placeholder. Declared defaults are preferred where
    /// available so any value-shaped check sees a legal value; value-format violations are
    /// filtered out by the callers regardless, because callers supply names, not values.
    /// </summary>
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

    /// <summary>
    /// Returns the supplied parameters whose value domain the catalog enumerates, keeping the
    /// cross-product bounded; parameters beyond the cap keep their base value.
    /// </summary>
    private static List<(string Name, IReadOnlyList<string> Values)> VariableDomains(
        ProcessDefinition definition,
        HashSet<string> supplied)
    {
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

        return variable;
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

        List<GeoprocessingValidationFailure>? universal = null;
        foreach (var assignment in Assignments(VariableDomains(definition, supplied)))
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

    /// <summary>
    /// Returns true when a non-missing violation is caused by the set of parameters that are
    /// present rather than by a value this probe fabricated. Presence shows up in two
    /// directions and both are real submit-time rejections:
    /// <list type="bullet">
    /// <item>a conflict between supplied inputs — withdrawing one clears it (the
    /// mutually-exclusive <c>connectionName</c>/<c>connectionId</c> pair);</item>
    /// <item>an unsatisfied branch requirement — supplying one more clears it. The canonical
    /// validator raises several of these as <c>INVALID_PARAMETER_VALUE</c> rather than
    /// <c>MISSING_REQUIRED_PARAMETER</c>: <c>conversion.rasterize</c>'s "exactly one of
    /// 'burnValue' or 'attribute'" and its cellSize-or-width+height grid rule, and
    /// <c>raster.interpolate-idw</c>'s width/height pair. Considering only withdrawal would
    /// discard those and certify a mapping the submit path rejects.</item>
    /// </list>
    /// A complaint about a substituted value survives both tests.
    /// </summary>
    private bool IsPresenceDependent(
        ProcessDefinition definition,
        Dictionary<string, string> inputs,
        GeoprocessingValidationFailure violation)
        => IsClearedByWithdrawal(definition.ProcessId, inputs, violation)
            || IsClearedByAddition(definition, inputs, violation);

    /// <summary>
    /// Withdraws each other supplied parameter in turn: if the violation disappears, the
    /// supplied inputs conflict. If it survives every withdrawal, this test proves nothing.
    /// </summary>
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

    /// <summary>
    /// Supplies each unmapped parameter in turn: if that leaves the violation's field wholly
    /// clean, the failure was caused by the parameter's ABSENCE — an unsatisfied branch
    /// requirement the mapping can never satisfy, because an unmapped parameter is never
    /// supplied at submit time.
    /// </summary>
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

    /// <summary>
    /// Value used when the probe supplies a parameter the mapping omits: the declared default,
    /// else the first declared allowed value, else a non-blank placeholder that satisfies every
    /// scalar type check.
    /// </summary>
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
        // satisfies ProcessPlanValidator could still be rejected - notably the sync-only
        // process ids that are not job-dispatchable at all.
        var (directSubmitViolations, _) = DirectSubmitPlanValidator.Evaluate(plan);
        violations.AddRange(directSubmitViolations.Where(candidate =>
            !violations.Any(existing =>
                string.Equals(existing.Code, candidate.Code, StringComparison.Ordinal)
                && string.Equals(existing.FieldPath, candidate.FieldPath, StringComparison.Ordinal))));

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
