// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Anti-drift coverage for the catalog-level value domains (#3048): every finite token set the
/// canonical <see cref="ProcessPlanValidator"/> enforces must be published as the owning
/// parameter's <c>AllowedValues</c>, and every published value must be one the validator
/// accepts. The assertions are BEHAVIORAL — they drive the real validator and the real
/// <see cref="BuiltInProcessCatalog"/> rather than comparing two hand-maintained lists — so a
/// future process that adds an enum rule without publishing it fails here instead of silently
/// under-describing the parameter and forcing the translation lane back onto its conservative
/// answer. Fully offline: no Docker, no Redis, no job store.
/// </summary>
public sealed class ProcessValueDomainDriftTests
{
    private const string StepId = "domain-probe";

    private readonly BuiltInProcessCatalog _catalog = new();

    /// <summary>
    /// Structurally different, individually legal-looking values. A closed token set rejects
    /// all three; a format rule (a GUID, an identifier, a numeric list) accepts at least one or
    /// rejects it for a reason other than set membership. Mirrors the sentinels
    /// <c>ProcessConditionalInputProbe</c> uses, so this test classifies parameters exactly the
    /// way the probe does.
    /// </summary>
    private static readonly string[] ForeignTokens =
    [
        "honuaProbeSentinel",
        "8f0a1c4e-6f1d-4a3a-9c2b-0d5e7f9a1b3c",
        "0,0,1,1"
    ];

    /// <summary>
    /// Token sets the validator enforces that are deliberately NOT published as
    /// <c>AllowedValues</c>. Each entry needs a reason that survives review, because an
    /// unpublished domain costs the translation lane its exact per-branch answer.
    /// </summary>
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
            + "GPServer choiceList, OGC JSON Schema enum and the toolbox translation probe all see it; "
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
    // Published metadata surfaces (#3048 acceptance: GPServer choiceList / OGC enum)
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ProcessSchema_NewlyEnumeratedDiscriminator_EmitsJsonSchemaEnum()
    {
        // The OGC API Processes description is generated from the shared catalog, so publishing
        // the domain must surface it as a JSON Schema enum with no protocol-side change.
        var definition = _catalog.GetProcess("analytics.cluster-managed");
        definition.Should().NotBeNull();

        using var document = JsonDocument.Parse(ProcessSchemaJsonGenerator.Generate(definition!));

        var algorithm = document.RootElement
            .GetProperty("inputs")
            .GetProperty("properties")
            .GetProperty("algorithm");

        algorithm.TryGetProperty("enum", out var choices).Should().BeTrue(
            "'algorithm' is the discriminator ProcessPlanValidator branches on and now declares "
            + "AllowedValues, so the generated schema must enumerate it");
        choices.EnumerateArray().Select(choice => choice.GetString())
            .Should().BeEquivalentTo(ProcessValueDomains.ClusterAlgorithm);
    }

    [UnitTest]
    public void ProcessSchema_UnpublishedDomain_EmitsNoJsonSchemaEnum()
    {
        // The inverse guard: transform.computed-field's 'op' is deliberately unpublished, so the
        // schema must NOT claim a domain the process exceeds.
        var definition = _catalog.GetProcess("transform.computed-field");
        definition.Should().NotBeNull();

        using var document = JsonDocument.Parse(ProcessSchemaJsonGenerator.Generate(definition!));

        document.RootElement.GetProperty("inputs").GetProperty("properties").GetProperty("op")
            .TryGetProperty("enum", out _).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool IsDeliberatelyUnpublished(ProcessDefinition definition, ProcessParameterSpec parameter)
        => UnpublishedDomains.Any(entry =>
            string.Equals(entry.ProcessId, definition.ProcessId, StringComparison.Ordinal)
            && string.Equals(entry.Parameter, parameter.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the canonical validator with <paramref name="value"/> as the ONLY supplied input and
    /// reports whether the parameter was refused for falling outside a closed token set (as
    /// opposed to a type, format, range or structural complaint). Other parameters' failures are
    /// irrelevant here and are filtered out by field path.
    /// </summary>
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
