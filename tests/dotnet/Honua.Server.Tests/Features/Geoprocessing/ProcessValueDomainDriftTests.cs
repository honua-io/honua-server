// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class ProcessValueDomainDriftTests
{
    private const string StepId = "domain-probe";

    private readonly BuiltInProcessCatalog _catalog = new();

    private static readonly string[] ForeignTokens =
    [
        "honuaProbeSentinel",
        "8f0a1c4e-6f1d-4a3a-9c2b-0d5e7f9a1b3c",
        "0,0,1,1"
    ];

    private static readonly (string ProcessId, string Parameter, string Reason)[] UnpublishedDomains =
    [
        ("raster.zonal-statistics", "statistics",
            "the parameter takes a COMMA-SEPARATED list of stat names (its own default is "
            + "'count,mean,stddev,min,max,sum'), so a single-token choice list would reject "
            + "every legal value including that default"),
        ("transform.computed-field", "op",
            "ComputedFieldTransformExecutor also honors op=expression, which the validator's set "
            + "omits; publishing the narrower set would advertise a domain the process actually "
            + "exceeds, and widening the validator is a runtime-behavior change out of scope here"),
    ];

    // -----------------------------------------------------------------------
    // Enforced domain -> published domain
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Catalog_EveryValidatorEnforcedTokenDomain_IsPublishedAsAllowedValues()
    {
        var undeclared = new List<string>();

        foreach (var definition in _catalog.ListProcesses())
        {
            foreach (var parameter in definition.Parameters)
            {
                if (parameter.AllowedValues is { Count: > 0 } || IsDeliberatelyUnpublished(definition, parameter))
                {
                    continue;
                }

                if (ForeignTokens.All(token => RejectsAsClosedValueSet(definition, parameter.Name, token)))
                {
                    undeclared.Add($"{definition.ProcessId}.{parameter.Name}");
                }
            }
        }

        undeclared.Should().BeEmpty(
            "every finite token set ProcessPlanValidator enforces must be published as AllowedValues so "
            + "GPServer choiceList and the toolbox translation probe all see it; "
            + "add the domain to ProcessValueDomains and reference it from BuiltInProcessCatalog, or "
            + "document an exclusion in UnpublishedDomains");
    }

    // -----------------------------------------------------------------------
    // Published domain -> enforced domain
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Catalog_EveryPublishedAllowedValue_IsAcceptedByTheValidator()
    {
        var rejected = new List<string>();

        foreach (var definition in _catalog.ListProcesses())
        {
            foreach (var parameter in definition.Parameters)
            {
                if (parameter.AllowedValues is not { Count: > 0 } allowed)
                {
                    continue;
                }

                rejected.AddRange(allowed
                    .Where(value => RejectsAsClosedValueSet(definition, parameter.Name, value))
                    .Select(value => $"{definition.ProcessId}.{parameter.Name}='{value}'"));
            }
        }

        rejected.Should().BeEmpty(
            "a published choice value the canonical validator refuses would advertise a branch the "
            + "submit path rejects");
    }

    [UnitTest]
    public void Catalog_EveryDefaultValue_IsInsideItsPublishedDomain()
    {
        var outside = new List<string>();

        foreach (var definition in _catalog.ListProcesses())
        {
            foreach (var parameter in definition.Parameters)
            {
                if (parameter.AllowedValues is not { Count: > 0 } allowed
                    || parameter.DefaultValue is not { } declaredDefault)
                {
                    continue;
                }

                if (!allowed.Contains(declaredDefault, StringComparer.OrdinalIgnoreCase))
                {
                    outside.Add($"{definition.ProcessId}.{parameter.Name}='{declaredDefault}'");
                }
            }
        }

        outside.Should().BeEmpty(
            "a default outside its own choice list is rejected by the GPServer GPChoice check before "
            + "the caller ever supplies a value");
    }

    [UnitTest]
    public void UnpublishedDomains_AreStillEnforcedAndStillUnpublished()
    {
        foreach (var (processId, parameterName, reason) in UnpublishedDomains)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull("'{0}' is listed as an unpublished domain", processId);

            var parameter = definition!.Parameters.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, parameterName, StringComparison.OrdinalIgnoreCase));
            parameter.Should().NotBeNull(
                "'{0}.{1}' is listed as an unpublished domain", processId, parameterName);

            parameter!.AllowedValues.Should().BeNullOrEmpty(
                "'{0}.{1}' now publishes a domain, so its exclusion ({2}) is stale and must be removed",
                processId, parameterName, reason);

            ForeignTokens.Any(token => RejectsAsClosedValueSet(definition!, parameterName, token))
                .Should().BeTrue(
                    "'{0}.{1}' no longer enforces a closed token set, so its exclusion is stale",
                    processId, parameterName);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool IsDeliberatelyUnpublished(ProcessDefinition definition, ProcessParameterSpec parameter)
        => UnpublishedDomains.Any(entry =>
            string.Equals(entry.ProcessId, definition.ProcessId, StringComparison.Ordinal)
            && string.Equals(entry.Parameter, parameter.Name, StringComparison.OrdinalIgnoreCase));

    private bool RejectsAsClosedValueSet(ProcessDefinition definition, string parameterName, string value)
    {
        var plan = new AnalysisPlan
        {
            PlanId = StepId,
            IntentId = StepId,
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = StepId,
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = definition.ProcessId,
                    Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [parameterName] = value
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        return violations.Any(failure =>
            string.Equals(failure.FieldPath, $"steps[{StepId}].inputs.{parameterName}", StringComparison.Ordinal)
            && ProcessPlanValidator.IsClosedValueSetRejection(failure));
    }
}
