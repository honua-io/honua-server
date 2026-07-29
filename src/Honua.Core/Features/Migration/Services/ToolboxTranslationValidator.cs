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
    /// Optional seam onto the canonical plan validator's presence-based input requirements.
    /// When supplied, a mapping that would be rejected at submit time (for example because
    /// it satisfies no member of a mutually-substitutable input group) is reported instead
    /// of being certified. When <c>null</c>, only static <c>Required</c> flags are checked.
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
        var unmappedRequired = definition.Parameters.Where(parameter =>
            parameter.Required
            && parameter.DefaultValue is null
            && !mappedTargets.Contains(parameter.Name));

        foreach (var parameter in unmappedRequired)
        {
            missingRequired = true;
            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.MissingRequiredParameter,
                Message = $"Required parameter '{parameter.Name}' of process '{definition.ProcessId}' has no default and is not mapped; the tool cannot execute.",
                ParameterName = parameter.Name
            });
        }

        // The probe can only exercise the branch selected by the values it substitutes, so a
        // parameter that is neither mapped nor defaulted leaves its value undetermined: a
        // caller-supplied discriminator could select a branch that requires it (for example
        // analytics.cluster-managed requires 'k' only when algorithm=kmeans, and the
        // catalog does not enumerate that parameter's legal values). Certifying such a
        // mapping executable would over-claim, so it is reported for review instead.
        var undetermined = definition.Parameters
            .Where(parameter => parameter.DefaultValue is null && !mappedTargets.Contains(parameter.Name))
            .Select(parameter => parameter.Name)
            .ToArray();

        // Static Required flags are not the whole admissibility contract: processes declare
        // mutually-substitutable optional inputs (the raster source/layerId/rasterId trio)
        // and mutually-exclusive ones (connectionName XOR connectionId) that only the
        // canonical plan validator enforces at submit time. Ask that validator through the
        // shared probe rather than re-implementing its rules here, so a tool this report
        // certifies is one the submit path will actually accept.
        if (!missingRequired && conditionalInputProbe is not null)
        {
            var suppliedNames = bindings.Select(binding => binding.TargetParameter).ToArray();
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

        if (!missingRequired && undetermined.Length > 0)
        {
            issues.Add(new ToolboxTranslationIssue
            {
                Code = ToolboxTranslationIssueCodes.UnverifiableConditionalBranches,
                Message = $"Parameter(s) {string.Join(", ", undetermined)} of process '{definition.ProcessId}' are neither mapped nor defaulted, so branch-dependent requirements cannot be proven for every caller-supplied value; review before treating this tool as executable.",
                ParameterName = undetermined[0]
            });
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
