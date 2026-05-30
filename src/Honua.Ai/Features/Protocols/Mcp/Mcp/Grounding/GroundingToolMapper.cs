// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Geoprocessing;

namespace Honua.Server.Features.Protocols.Mcp.Grounding;

/// <summary>
/// Converts between MCP wire shapes and canonical grounding-domain records.
/// Validation failures are surfaced as
/// <see cref="GeoprocessingValidationException"/> so they flow through the
/// existing MCP error mapper.
/// </summary>
internal static class GroundingToolMapper
{
    /// <summary>
    /// Maps <see cref="McpGroundCandidatesArgument"/> → canonical
    /// <see cref="GroundingRequest"/>.
    /// </summary>
    public static GroundingRequest ToDomain(McpGroundCandidatesArgument? argument)
    {
        if (argument is null)
        {
            throw new GeoprocessingValidationException("Tool arguments are required.");
        }

        if (string.IsNullOrWhiteSpace(argument.Goal))
        {
            throw new GeoprocessingValidationException("Grounding request goal must be a non-empty string.");
        }

        return new GroundingRequest
        {
            Goal = argument.Goal,
            WorkflowFamilyHint = ParseWorkflowFamilyHint(argument.WorkflowFamilyHint),
            AssumptionPolicy = ParseAssumptionPolicy(argument.AssumptionPolicy),
            Constraints = ToDomain(argument.Constraints),
            ExplicitInputs = NormalizeExplicitInputs(argument.ExplicitInputs),
            Context = ToDomain(argument.Context),
            // Treat blank/whitespace `intentId` as "omitted" on the initial grounding
            // call so GroundingService allocates a fresh id instead of propagating an
            // empty string into the draft + clarification envelope (which the clarify
            // tool would then reject). Intent parity for clarification turns is
            // enforced separately in ToDomain(McpClarifyIntentArgument).
            IntentId = string.IsNullOrWhiteSpace(argument.IntentId) ? null : argument.IntentId
        };
    }

    /// <summary>
    /// Maps <see cref="McpClarifyIntentArgument"/> → canonical
    /// <see cref="GroundingRequest"/>. The clarifier tool requires both
    /// <c>intentId</c> and a <c>response.answers</c> payload; either missing
    /// surfaces as <c>invalid_argument</c>.
    /// </summary>
    public static GroundingRequest ToDomain(McpClarifyIntentArgument? argument)
    {
        if (argument is null)
        {
            throw new GeoprocessingValidationException("Tool arguments are required.");
        }

        if (string.IsNullOrWhiteSpace(argument.IntentId))
        {
            throw new GeoprocessingValidationException("Clarification turn requires a non-empty intentId.");
        }

        if (argument.Response is null || argument.Response.Answers is null || argument.Response.Answers.Count == 0)
        {
            throw new GeoprocessingValidationException("Clarification turn requires response.answers to be supplied.");
        }

        EnsureAnswersContainNonBlankValues(argument.Response.Answers);

        var clarification = new ClarificationResponse
        {
            IntentId = argument.IntentId,
            Answers = argument.Response.Answers
        };

        // Goal may be omitted on a pure clarification turn — the caller is
        // expected to re-send the same goal text so the service has enough
        // signal to re-run the ranking. If goal is blank we fail clearly
        // instead of feeding an empty goal to the engine.
        if (string.IsNullOrWhiteSpace(argument.Goal))
        {
            throw new GeoprocessingValidationException(
                "Clarification turn requires the original goal text to be re-sent so the service can re-rank.");
        }

        return new GroundingRequest
        {
            Goal = argument.Goal,
            WorkflowFamilyHint = ParseWorkflowFamilyHint(argument.WorkflowFamilyHint),
            AssumptionPolicy = ParseAssumptionPolicy(argument.AssumptionPolicy),
            Constraints = ToDomain(argument.Constraints),
            ExplicitInputs = NormalizeExplicitInputs(argument.ExplicitInputs),
            Context = ToDomain(argument.Context),
            IntentId = argument.IntentId,
            ClarificationResponse = clarification
        };
    }

    /// <summary>
    /// Maps a domain <see cref="GroundingResult"/> to the MCP wire output.
    /// </summary>
    public static McpGroundingOutput ToWire(GroundingResult result)
    {
        return new McpGroundingOutput
        {
            WorkflowFamily = ToWire(result.WorkflowFamily),
            DraftIntent = ToWire(result.DraftIntent),
            Candidates = ToWire(result.Candidates),
            Clarification = result.Clarification is { } clarification ? ToWire(clarification) : null,
            Engine = result.Engine
        };
    }

    private static List<string> NormalizeExplicitInputs(IReadOnlyList<string>? inputs)
    {
        // Drop blank / whitespace-only entries and trim surrounding whitespace on
        // retained entries so IntentDrafter cannot lock a padded value like
        // "  parcels-layer  " into PublishIntent.SourceId (which would break
        // downstream lookups and suppress the publish.source clarification).
        // Treat the overall field as optional — fully blank lists collapse to an
        // empty list exactly like an omitted field.
        if (inputs is null || inputs.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var value = inputs[i];
            if (!string.IsNullOrWhiteSpace(value))
            {
                normalized.Add(value.Trim());
            }
        }
        return normalized;
    }

    private static WorkflowFamily? ParseWorkflowFamilyHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        // Enum.TryParse accepts underlying numeric strings (e.g. "999") as
        // undefined enum values, so pair it with Enum.IsDefined to keep
        // non-contract strings from leaking into workflowFamily.value.
        if (!Enum.TryParse<WorkflowFamily>(hint, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new GeoprocessingValidationException(
                $"Unknown workflowFamilyHint '{hint}'. Expected one of: {string.Join(", ", Enum.GetNames<WorkflowFamily>())}.");
        }

        return parsed;
    }

    private static AssumptionPolicy ParseAssumptionPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AssumptionPolicy.AskWhenMaterial;
        }

        if (!Enum.TryParse<AssumptionPolicy>(value, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new GeoprocessingValidationException(
                $"Unknown assumptionPolicy '{value}'. Expected one of: {string.Join(", ", Enum.GetNames<AssumptionPolicy>())}.");
        }

        return parsed;
    }

    private static IntentConstraints? ToDomain(McpIntentConstraintsInput? input)
    {
        if (input is null)
        {
            return null;
        }

        return new IntentConstraints
        {
            AreaOfInterest = input.AreaOfInterest,
            SpatialReferenceId = input.SpatialReferenceId,
            TimeWindowStart = input.TimeWindowStart,
            TimeWindowEnd = input.TimeWindowEnd,
            Units = input.Units
        };
    }

    private static void EnsureAnswersContainNonBlankValues(
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        foreach (var (questionId, values) in answers)
        {
            // Blank/whitespace question ids are dropped silently by
            // ClarificationAnswerResolver.Parse, so reject them at the MCP
            // boundary to surface the caller mistake as invalid_argument
            // instead of a no-op clarification turn.
            if (string.IsNullOrWhiteSpace(questionId))
            {
                throw new GeoprocessingValidationException(
                    "Clarification answer questionId must be a non-blank string.");
            }

            if (values.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                continue;
            }

            throw new GeoprocessingValidationException(
                $"Clarification answer '{questionId}' must include at least one non-blank value.");
        }
    }

    private static CallerContext ToDomain(McpCallerContextInput? input)
    {
        if (input is null)
        {
            return new CallerContext();
        }

        return new CallerContext
        {
            WorkspaceId = input.WorkspaceId,
            PriorIntentId = input.PriorIntentId,
            PromotionScope = input.PromotionScope
        };
    }

    private static McpIntentConstraintsInput? ToWire(IntentConstraints? constraints)
    {
        if (constraints is null)
        {
            return null;
        }

        return new McpIntentConstraintsInput
        {
            AreaOfInterest = constraints.AreaOfInterest,
            SpatialReferenceId = constraints.SpatialReferenceId,
            TimeWindowStart = constraints.TimeWindowStart,
            TimeWindowEnd = constraints.TimeWindowEnd,
            Units = constraints.Units
        };
    }

    private static McpWorkflowFamilyClassification ToWire(WorkflowFamilyClassification classification) => new()
    {
        Value = classification.Value.ToString(),
        Confidence = classification.Confidence,
        Evidence = classification.Evidence
    };

    private static McpDraftIntent ToWire(DraftIntent draft) => new()
    {
        IntentId = draft.IntentId,
        Goal = draft.Goal,
        WorkflowFamily = draft.WorkflowFamily.ToString(),
        RequestedOutputs = draft.RequestedOutputs.Select(k => k.ToString()).ToArray(),
        AssumptionPolicy = draft.AssumptionPolicy.ToString(),
        Analysis = draft.Analysis is { } analysis ? ToWire(analysis) : null,
        Publishing = draft.Publishing is { } publishing ? ToWire(publishing) : null,
        Provenance = ToWire(draft.Provenance)
    };

    private static McpAnalysisIntentView ToWire(AnalysisIntent analysis) => new()
    {
        IntentId = analysis.IntentId,
        Goal = analysis.Goal,
        Mode = analysis.Mode,
        RequestedOutputs = analysis.RequestedOutputs.Select(k => k.ToString()).ToArray(),
        Constraints = ToWire(analysis.Constraints),
        Inputs = analysis.Inputs,
        AssumptionPolicy = analysis.AssumptionPolicy.ToString()
    };

    private static McpPublishIntentView ToWire(Honua.Core.Features.Publishing.Domain.PublishIntent publishing) => new()
    {
        IntentId = publishing.IntentId,
        SourceKind = publishing.SourceKind.ToString(),
        SourceId = publishing.SourceId,
        TargetKind = publishing.TargetKind.ToString(),
        Status = publishing.Status.ToString()
    };

    private static McpGroundingProvenance ToWire(ProvenanceRecord provenance) => new()
    {
        Sources = provenance.Sources
            .Select(s => new McpGroundingProvenanceSource
            {
                SourceId = s.SourceId,
                Description = s.Description
            })
            .ToArray(),
        ProcessDefinitions = provenance.ProcessDefinitions,
        Assumptions = provenance.Assumptions,
        ClarificationsAsked = provenance.ClarificationsAsked,
        ClarificationsAnswered = provenance.ClarificationsAnswered
    };

    private static McpCandidateRanking ToWire(CandidateRanking ranking) => new()
    {
        Datasets = ranking.Datasets.Select(ToWire).ToArray(),
        Processes = ranking.Processes.Select(ToWire).ToArray(),
        Templates = ranking.Templates.Select(ToWire).ToArray(),
        Styles = ranking.Styles.Select(ToWire).ToArray(),
        Deployments = ranking.Deployments.Select(ToWire).ToArray()
    };

    private static McpGroundingCandidate ToWire(GroundingCandidate candidate) => new()
    {
        Id = candidate.Id,
        Kind = candidate.Kind.ToString(),
        DisplayName = candidate.DisplayName,
        Score = candidate.Score,
        ConfidenceBand = candidate.ConfidenceBand.ToString(),
        Evidence = candidate.Evidence
    };

    private static McpClarificationEnvelope ToWire(ClarificationRequest clarification) => new()
    {
        IntentId = clarification.IntentId,
        ReasonCodes = clarification.ReasonCodes.Select(r => r.ToString()).ToArray(),
        Questions = clarification.Questions.Select(ToWire).ToArray()
    };

    private static McpClarificationQuestionView ToWire(ClarificationQuestion question) => new()
    {
        QuestionId = question.QuestionId,
        Kind = question.Kind.ToString(),
        Prompt = question.Prompt,
        Options = question.Options is { Count: > 0 } options
            ? options.Select(o => new McpClarificationOptionView { Id = o.Id, Label = o.Label }).ToArray()
            : null
    };
}
