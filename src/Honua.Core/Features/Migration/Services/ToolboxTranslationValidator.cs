// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Server-authoritative round-trip validation for SDK-translated toolbox manifests (#2145).
/// The canonical process catalog is the single source of truth for executable signatures, so
/// the round-trip proof lives here rather than in the SDK: every proposed tool mapping is
/// resolved against <see cref="IProcessCatalog"/>, valid mappings are echoed back with their
/// canonical parameter signature, and untranslatable constructs become explicit per-tool
/// issues instead of silently dropped or stubbed-as-executable tools.
/// </summary>
public static class ToolboxTranslationValidator
{
    /// <summary>
    /// Validates a translated toolbox manifest against the canonical process catalog.
    /// </summary>
    /// <param name="manifest">SDK-emitted translation manifest. Must be structurally valid
    /// (non-blank toolbox name, known source format, at least one tool with a non-blank
    /// name); protocol adapters enforce structure before calling.</param>
    /// <param name="catalog">Canonical process catalog.</param>
    /// <param name="conditionalInputProbe">
    /// Optional seam onto the canonical plan validator's presence-based input requirements
    /// and the runtime availability of the target process. When supplied, a mapping that
    /// would be rejected at submit time (for example because it satisfies no member of a
    /// mutually-substitutable input group) or that targets a process whose executor cannot
    /// complete any job is reported instead of being certified, and it also decides which
    /// unmapped parameters are genuinely branch-dependent rather than unconditionally
    /// optional. When <c>null</c>, only static <c>Required</c> flags are checked and every
    /// unmapped, defaultless parameter is reported, because the two cases cannot be told
    /// apart without the validator.
    /// </param>
    /// <returns>Per-tool classification report with round-tripped signatures.</returns>
    public static ToolboxTranslationReport Validate(
        ToolboxTranslationManifest manifest,
        IProcessCatalog catalog,
        IProcessConditionalInputProbe? conditionalInputProbe = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(catalog);

        var tools = new ToolboxToolTranslation[manifest.Tools.Length];
        var translated = 0;
        var partial = 0;
        var unsupported = 0;

        for (var i = 0; i < manifest.Tools.Length; i++)
        {
            var tool = ValidateTool(manifest.Tools[i], catalog, conditionalInputProbe);
            tools[i] = tool;
            switch (tool.Classification)
            {
                case ToolboxToolClassifications.Translated:
                    translated++;
                    break;
                case ToolboxToolClassifications.PartiallyTranslated:
                    partial++;
                    break;
                default:
                    unsupported++;
                    break;
            }
        }

        return new ToolboxTranslationReport
        {
            ToolboxName = manifest.ToolboxName.Trim(),
            SourceFormat = manifest.SourceFormat.Trim().ToLowerInvariant(),
            Summary = new ToolboxTranslationSummary
            {
                ToolCount = tools.Length,
                TranslatedCount = translated,
                PartiallyTranslatedCount = partial,
                UnsupportedCount = unsupported
            },
            Tools = tools
        };
    }

    private static ToolboxToolTranslation ValidateTool(
        ToolboxToolDescriptor descriptor,
        IProcessCatalog catalog,
        IProcessConditionalInputProbe? conditionalInputProbe)
    {
        var issues = new List<ToolboxTranslationIssue>();

        foreach (var construct in descriptor.UnsupportedConstructs.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.UnsupportedConstruct,
                Message = $"Source construct cannot be translated: {construct.Trim()}."
            });
        }

        var targetProcessId = descriptor.TargetProcessId?.Trim();
        if (string.IsNullOrEmpty(targetProcessId))
        {
            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.NoNativeExecutor,
                Message = "The scanner proposed no native Honua process for this tool; it cannot be executed."
            });

            return BuildResult(descriptor, ToolboxToolClassifications.Unsupported, processId: null, [], issues);
        }

        var definition = catalog.GetProcess(targetProcessId);
        if (definition is null)
        {
            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.UnknownProcess,
                Message = $"Target process '{targetProcessId}' does not exist in the canonical process catalog."
            });

            return BuildResult(descriptor, ToolboxToolClassifications.Unsupported, processId: null, [], issues);
        }

        var bindings = new List<ToolboxParameterBinding>(descriptor.ParameterMappings.Length);
        var mappedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in descriptor.ParameterMappings)
        {
            var target = FindParameter(definition, mapping.TargetParameter);
            if (target is null)
            {
                issues.Add(new ToolboxTranslationIssue
                {
                    Code = ToolboxTranslationIssueCodes.UnknownTargetParameter,
                    Message = $"Process '{definition.ProcessId}' declares no parameter named '{mapping.TargetParameter}'.",
                    ParameterName = mapping.SourceName
                });
                continue;
            }

            if (!mappedTargets.Add(target.Name))
            {
                issues.Add(new ToolboxTranslationIssue
                {
                    Code = ToolboxTranslationIssueCodes.DuplicateTargetParameter,
                    Message = $"Multiple source parameters map to canonical parameter '{target.Name}'.",
                    ParameterName = mapping.SourceName
                });
                continue;
            }

            bindings.Add(new ToolboxParameterBinding
            {
                SourceName = mapping.SourceName,
                TargetParameter = target.Name,
                ValueType = target.ValueType.ToString(),
                Required = target.Required
            });
        }

        var missingRequired = false;
        // A declared-required parameter must be mapped even when it carries a default: the
        // canonical validator requires the input *key* to be present regardless, so a
        // default does not stand in for a missing mapping at submit time.
        var unmappedRequired = definition.Parameters.Where(parameter =>
            parameter.Required && !mappedTargets.Contains(parameter.Name));

        foreach (var parameter in unmappedRequired)
        {
            missingRequired = true;
            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.MissingRequiredParameter,
                Message = $"Required parameter '{parameter.Name}' of process '{definition.ProcessId}' is not mapped; the canonical validator requires the input key to be present even when the parameter declares a default, so the tool cannot execute.",
                ParameterName = parameter.Name
            });
        }

        var suppliedNames = bindings.Select(binding => binding.TargetParameter).ToArray();

        // Static Required flags are not the whole admissibility contract: processes declare
        // mutually-substitutable optional inputs (the raster source/layerId/rasterId trio)
        // and mutually-exclusive ones (connectionName XOR connectionId) that only the
        // canonical plan validator enforces at submit time. Ask that validator through the
        // shared probe rather than re-implementing its rules here, so a tool this report
        // certifies is one the submit path will actually accept.
        if (!missingRequired && conditionalInputProbe is not null)
        {
            foreach (var violation in conditionalInputProbe.FindAdmissibilityViolations(definition.ProcessId, suppliedNames))
            {
                missingRequired = true;
                issues.Add(new ToolboxTranslationIssue
                {
                    Code = violation.Kind == ProcessAdmissibilityViolationKind.NotJobExecutable
                        ? ToolboxTranslationIssueCodes.ProcessNotJobExecutable
                        : ToolboxTranslationIssueCodes.UnsatisfiedConditionalInputs,
                    Message = $"Canonical submit validation would reject this mapping: {violation.Message}"
                });
            }
        }

        if (!missingRequired)
        {
            // The catalog models no conditional requirements at all — ProcessParameterSpec
            // carries only Required/DefaultValue/AllowedValues — so they live exclusively as
            // per-process rules inside the canonical plan validator. Ask that validator, through
            // the probe, which unmapped parameters it can actually require under some admissible
            // branch instead of treating every optional omission as one: the submit path accepts
            // geometry.dissolve without 'groupKeys', so downgrading that mapping would report an
            // executable tool as unproven.
            //
            // Where the discriminator's domain IS published (#3048), the probe enumerates every
            // branch and the answer is exact: 'k' is required under algorithm=kmeans and under
            // nothing else, so the report names the branch instead of shrugging. The conservative
            // downgrade survives only for a genuinely unenumerable domain
            // (transform.computed-field's 'op'), where certifying an unvisited branch would
            // over-claim executability.
            AddConditionalBranchIssues(definition, conditionalInputProbe, suppliedNames, issues);

            IReadOnlyList<string> unverifiable = conditionalInputProbe?.FindUnverifiableConditionalParameters(
                    definition.ProcessId, suppliedNames)
                // With no probe the two cases cannot be told apart, so the pessimistic answer
                // stands rather than guessing which omissions are branch-dependent.
                ?? definition.Parameters
                    .Where(parameter => parameter.DefaultValue is null && !mappedTargets.Contains(parameter.Name))
                    .Select(parameter => parameter.Name)
                    .ToArray();

            if (unverifiable.Count > 0)
            {
                issues.Add(new ToolboxTranslationIssue
                {
                    Code = ToolboxTranslationIssueCodes.UnverifiableConditionalBranches,
                    Message = $"Parameter(s) {string.Join(", ", unverifiable)} of process '{definition.ProcessId}' are neither mapped nor defaulted and the canonical validator can require them on a branch this report cannot pin down, so execution cannot be proven for every caller-supplied value; review before treating this tool as executable.",
                    ParameterName = unverifiable[0]
                });
            }
        }

        // A tool that cannot supply a required parameter is not executable against the
        // native process, so it is unsupported rather than partially translated.
        var classification = missingRequired
            ? ToolboxToolClassifications.Unsupported
            : issues.Count > 0
                ? ToolboxToolClassifications.PartiallyTranslated
                : ToolboxToolClassifications.Translated;

        return BuildResult(descriptor, classification, definition.ProcessId, bindings, issues);
    }

    /// <summary>
    /// Adds one exact, branch-qualified issue per unmapped parameter the canonical validator
    /// requires on an enumerable discriminator branch. Grouped by parameter so a domain that
    /// spells the same branch several ways (<c>kmeans</c> / <c>k-means</c>) reads as one gap
    /// with several triggering values rather than as several gaps.
    /// </summary>
    private static void AddConditionalBranchIssues(
        ProcessDefinition definition,
        IProcessConditionalInputProbe? conditionalInputProbe,
        IReadOnlyCollection<string> suppliedNames,
        List<ToolboxTranslationIssue> issues)
    {
        var requirements = conditionalInputProbe?.FindConditionalBranchRequirements(
            definition.ProcessId, suppliedNames);
        if (requirements is not { Count: > 0 })
        {
            return;
        }

        // "Constrain the source value instead of mapping" is only sound advice when some
        // admissible discriminator value is left with no gap. Gaps are reported per PARAMETER,
        // so each group individually looks escapable even when the groups TOGETHER cover every
        // branch — e.g. a mapping supplying only `input` and `algorithm` omits `eps`/`minPoints`
        // on dbscan and `k` on kmeans, and then no value of `algorithm` executes at all.
        // Coverage is therefore computed across ALL requirements before the claim is made
        // (honua-server#3048 review).
        var remedy = DescribeBranchRemedy(definition, requirements);

        foreach (var group in requirements.GroupBy(
            requirement => requirement.ParameterName, StringComparer.OrdinalIgnoreCase))
        {
            var branches = group
                .Select(requirement => requirement.Branch)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.ConditionalBranchRequirement,
                Message = $"Parameter '{group.Key}' of process '{definition.ProcessId}' is neither mapped nor defaulted and the canonical validator requires it on branch(es) {string.Join("; ", branches)}. {remedy}",
                ParameterName = group.Key
            });
        }
    }

    /// <summary>
    /// Decides which remedy the branch-requirement issues may honestly recommend, by testing
    /// whether any discriminator has every one of its declared values covered by some gap.
    /// </summary>
    private static string DescribeBranchRemedy(
        ProcessDefinition definition,
        IReadOnlyList<ProcessBranchRequirement> requirements)
    {
        const string MapOrConstrain =
            "Every other admissible value executes, so map it (or constrain the source value) to certify this tool.";
        const string MapOnly =
            "Every admissible value of the discriminator has an unmapped requirement, so constraining the source value cannot certify this tool - the parameter must be mapped or defaulted.";
        const string MapUnqualified =
            "Map it, or constrain the source value to a branch that does not require it, to certify this tool.";

        var covered = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements)
        {
            var assignments = requirement.Branch.Split(',', StringSplitOptions.RemoveEmptyEntries);

            // A branch naming several discriminators at once only covers that COMBINATION, so
            // it says nothing definite about any single discriminator's value. Rather than
            // over- or under-claiming from a partial reading, drop to the unqualified remedy.
            if (assignments.Length != 1)
            {
                return MapUnqualified;
            }

            var separator = assignments[0].IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return MapUnqualified;
            }

            var name = assignments[0][..separator].Trim();
            var value = assignments[0][(separator + 1)..].Trim();

            if (!covered.TryGetValue(name, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                covered[name] = values;
            }

            values.Add(value);
        }

        foreach (var (name, values) in covered)
        {
            // Only a declared domain makes "every admissible value" a decidable claim; without
            // AllowedValues the branch space is open and no exhaustion can be proven.
            if (FindParameter(definition, name)?.AllowedValues is { Count: > 0 } allowed &&
                allowed.All(values.Contains))
            {
                return MapOnly;
            }
        }

        return MapOrConstrain;
    }

    private static ProcessParameterSpec? FindParameter(ProcessDefinition definition, string name)
    {
        foreach (var parameter in definition.Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }

    private static ToolboxToolTranslation BuildResult(
        ToolboxToolDescriptor descriptor,
        string classification,
        string? processId,
        List<ToolboxParameterBinding> bindings,
        List<ToolboxTranslationIssue> issues) =>
        new()
        {
            ToolName = descriptor.ToolName.Trim(),
            Classification = classification,
            ProcessId = processId,
            ParameterBindings = [.. bindings],
            Issues = [.. issues]
        };
}
