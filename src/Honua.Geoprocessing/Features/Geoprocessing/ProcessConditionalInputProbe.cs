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
    public IReadOnlyList<string> FindMissingRequiredInputs(
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
                    ProcessId = definition.ProcessId,
                    Inputs = inputs
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        // Only presence-based failures are meaningful here; placeholder values can trip
        // value-format rules that say nothing about whether the mapping is admissible.
        return [.. violations
            .Where(violation => string.Equals(
                violation.Code,
                MissingRequiredParameterCode,
                StringComparison.Ordinal))
            .Select(violation => violation.Message)];
    }
}
