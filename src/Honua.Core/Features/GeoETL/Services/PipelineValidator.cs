// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;

namespace Honua.Core.Features.GeoETL.Services;

/// <summary>
/// A single validation problem produced by <see cref="PipelineValidator"/>. Hard
/// failures block storage and execution; advisories are non-blocking (for example a
/// connector that requires a native worker profile that is not yet registered).
/// </summary>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="IsAdvisory">True when the problem is advisory and does not block.</param>
public sealed record PipelineValidationIssue(string Code, string Message, bool IsAdvisory);

/// <summary>
/// Result of validating a <see cref="PipelineDefinition"/>.
/// </summary>
/// <param name="Issues">All validation issues, hard and advisory.</param>
public sealed record PipelineValidationResult(IReadOnlyList<PipelineValidationIssue> Issues)
{
    /// <summary>
    /// True when there are no hard-failure issues. Advisory issues do not affect validity.
    /// </summary>
    public bool IsValid => !Issues.Any(i => !i.IsAdvisory);

    /// <summary>
    /// The subset of issues that block storage and execution.
    /// </summary>
    public IReadOnlyList<PipelineValidationIssue> Errors =>
        Issues.Where(i => !i.IsAdvisory).ToArray();

    /// <summary>
    /// The subset of issues that are advisory only.
    /// </summary>
    public IReadOnlyList<PipelineValidationIssue> Advisories =>
        Issues.Where(i => i.IsAdvisory).ToArray();
}

/// <summary>
/// Validates the structural shape and connector/transform resolvability of a pipeline
/// definition. Structural shape and component resolvability are hard fails; a connector
/// that needs a native worker profile produces an advisory only (CRUD stores regardless),
/// matching the three-layer capability-detection model in ADR-0038.
/// </summary>
public sealed class PipelineValidator(IPipelineComponentFactory factory)
{
    /// <summary>
    /// Validates the definition against the registered component factory.
    /// </summary>
    /// <param name="definition">Definition to validate.</param>
    /// <returns>The validation result.</returns>
    public PipelineValidationResult Validate(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = new List<PipelineValidationIssue>();

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            issues.Add(new PipelineValidationIssue("EMPTY_ID", "Pipeline id is required.", false));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new PipelineValidationIssue("EMPTY_NAME", "Pipeline name is required.", false));
        }

        if (definition.Stages.Count < 2)
        {
            issues.Add(new PipelineValidationIssue(
                "TOO_FEW_STAGES",
                "A pipeline must contain at least a source and a sink stage.",
                false));
            return new PipelineValidationResult(issues);
        }

        // First stage must be the source; last must be the sink; the middle must be transforms.
        if (definition.Stages[0].Kind != PipelineStageKind.Source)
        {
            issues.Add(new PipelineValidationIssue(
                "MISSING_SOURCE", "The first stage must be a source stage.", false));
        }

        if (definition.Stages[^1].Kind != PipelineStageKind.Sink)
        {
            issues.Add(new PipelineValidationIssue(
                "MISSING_SINK", "The last stage must be a sink stage.", false));
        }

        for (var i = 1; i < definition.Stages.Count - 1; i++)
        {
            if (definition.Stages[i].Kind != PipelineStageKind.Transform)
            {
                issues.Add(new PipelineValidationIssue(
                    "UNEXPECTED_STAGE",
                    $"Stage {i} must be a transform stage (only one source and one sink are allowed).",
                    false));
            }
        }

        ValidateSource(definition, issues);
        ValidateTransforms(definition, issues);
        ValidateSink(definition, issues);

        return new PipelineValidationResult(issues);
    }

    private void ValidateSource(PipelineDefinition definition, List<PipelineValidationIssue> issues)
    {
        var source = definition.Stages[0];
        if (source.Kind != PipelineStageKind.Source)
        {
            return;
        }

        if (source.Connector is null)
        {
            issues.Add(new PipelineValidationIssue(
                "SOURCE_CONFIG_MISSING", "Source stage has no connector configuration.", false));
            return;
        }

        var connector = factory.ResolveSource(source.Connector.Type);
        if (connector is null)
        {
            issues.Add(new PipelineValidationIssue(
                "UNKNOWN_SOURCE",
                $"Source connector type '{source.Connector.Type}' is not registered.",
                false));
            return;
        }

        if (connector.RuntimeProfile == ConnectorRuntimeProfile.RequiresNativeGeo)
        {
            issues.Add(new PipelineValidationIssue(
                "SOURCE_REQUIRES_NATIVE_WORKER",
                $"Source connector '{source.Connector.Type}' requires the native worker profile " +
                "(honua-worker-etl), which is not part of the Phase 1 serving image.",
                true));
        }
    }

    private void ValidateTransforms(PipelineDefinition definition, List<PipelineValidationIssue> issues)
    {
        foreach (var stage in definition.Transforms)
        {
            if (stage.Transform is null)
            {
                issues.Add(new PipelineValidationIssue(
                    "TRANSFORM_CONFIG_MISSING", "Transform stage has no transform configuration.", false));
                continue;
            }

            if (factory.ResolveTransform(stage.Transform.Type) is null)
            {
                issues.Add(new PipelineValidationIssue(
                    "UNKNOWN_TRANSFORM",
                    $"Transform type '{stage.Transform.Type}' is not registered.",
                    false));
            }
        }
    }

    private void ValidateSink(PipelineDefinition definition, List<PipelineValidationIssue> issues)
    {
        var sink = definition.Stages[^1];
        if (sink.Kind != PipelineStageKind.Sink)
        {
            return;
        }

        if (sink.Connector is null)
        {
            issues.Add(new PipelineValidationIssue(
                "SINK_CONFIG_MISSING", "Sink stage has no connector configuration.", false));
            return;
        }

        var connector = factory.ResolveSink(sink.Connector.Type);
        if (connector is null)
        {
            issues.Add(new PipelineValidationIssue(
                "UNKNOWN_SINK",
                $"Sink connector type '{sink.Connector.Type}' is not registered.",
                false));
            return;
        }

        if (connector.RuntimeProfile == ConnectorRuntimeProfile.RequiresNativeGeo)
        {
            issues.Add(new PipelineValidationIssue(
                "SINK_REQUIRES_NATIVE_WORKER",
                $"Sink connector '{sink.Connector.Type}' requires the native worker profile " +
                "(honua-worker-etl), which is not part of the Phase 1 serving image.",
                true));
        }
    }
}
