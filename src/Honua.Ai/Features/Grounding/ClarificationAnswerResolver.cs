// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Grounding;

internal sealed record AppliedClarificationAnswers
{
    public static readonly AppliedClarificationAnswers Empty = new();

    public WorkflowFamily? WorkflowFamilyOverride { get; init; }

    public string? PinnedDatasetId { get; init; }

    public string? PinnedProcessId { get; init; }

    public PublishTargetKind? PublishTargetOverride { get; init; }

    public string? ResolvedPublishSourceId { get; init; }

    public IReadOnlyDictionary<string, string> ResolvedParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlySet<string> AppliedQuestionIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

internal static class ClarificationAnswerResolver
{
    private const string WorkflowFamilyQuestionId = "workflow_family";
    private const string DatasetSelectionQuestionId = "dataset.selection";
    private const string ProcessSelectionQuestionId = "process.selection";
    private const string PublishTargetQuestionId = "publish.target";
    private const string PublishSourceQuestionId = "publish.source";
    private const string DestructiveConfirmQuestionId = "destructive.confirm";
    private const string WorkflowFamilyBlockedQuestionId = "workflow_family.blocked";
    private const string ParameterQuestionPrefix = "param.";

    public static AppliedClarificationAnswers Parse(ClarificationResponse? response)
    {
        if (response?.Answers is not { Count: > 0 } answers)
        {
            return AppliedClarificationAnswers.Empty;
        }

        WorkflowFamily? workflowFamilyOverride = null;
        string? pinnedDatasetId = null;
        string? pinnedProcessId = null;
        PublishTargetKind? publishTargetOverride = null;
        string? resolvedPublishSourceId = null;
        var resolvedParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var appliedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (questionId, values) in answers)
        {
            if (string.IsNullOrWhiteSpace(questionId))
            {
                continue;
            }

            var value = FirstNonBlank(values);
            if (value is null)
            {
                continue;
            }

            switch (questionId)
            {
                case WorkflowFamilyQuestionId:
                    workflowFamilyOverride = ParseEnumOrThrow<WorkflowFamily>(
                        questionId, value, "workflow family");
                    appliedIds.Add(questionId);
                    break;

                case PublishTargetQuestionId:
                    publishTargetOverride = ParseEnumOrThrow<PublishTargetKind>(
                        questionId, value, "publish target");
                    appliedIds.Add(questionId);
                    break;

                case PublishSourceQuestionId:
                    // Free-text or picked-from-options source id. The drafter
                    // treats it as the sourceId for the publish intent so the
                    // follow-up pass can produce a non-null `publishing` block
                    // even when `ExplicitInputs` is still empty.
                    resolvedPublishSourceId = value;
                    appliedIds.Add(questionId);
                    break;

                case DatasetSelectionQuestionId:
                    pinnedDatasetId = value;
                    // Applied flag is set only if the candidate is actually
                    // present in the ranking — the service adds it then.
                    break;

                case ProcessSelectionQuestionId:
                    pinnedProcessId = value;
                    break;

                case DestructiveConfirmQuestionId:
                case WorkflowFamilyBlockedQuestionId:
                    // Confirmation-style answers: any non-empty value counts
                    // as an acknowledgement. No further state to carry.
                    appliedIds.Add(questionId);
                    break;

                default:
                    if (questionId.StartsWith(ParameterQuestionPrefix, StringComparison.Ordinal)
                        && questionId.Length > ParameterQuestionPrefix.Length)
                    {
                        var parameterName = questionId.Substring(ParameterQuestionPrefix.Length);
                        resolvedParameters[parameterName] = value;
                        appliedIds.Add(questionId);
                    }
                    // Unknown question ids are ignored for forward compat.
                    break;
            }
        }

        return new AppliedClarificationAnswers
        {
            WorkflowFamilyOverride = workflowFamilyOverride,
            PinnedDatasetId = pinnedDatasetId,
            PinnedProcessId = pinnedProcessId,
            PublishTargetOverride = publishTargetOverride,
            ResolvedPublishSourceId = resolvedPublishSourceId,
            ResolvedParameters = resolvedParameters,
            AppliedQuestionIds = appliedIds
        };
    }

    public static IReadOnlyList<GroundingCandidate> ApplyPin(
        IReadOnlyList<GroundingCandidate> candidates,
        string? pinnedId,
        string questionId,
        HashSet<string> appliedIds)
    {
        if (string.IsNullOrWhiteSpace(pinnedId))
        {
            return candidates;
        }

        // An empty post-filter ranking with a nonblank pin means the caller
        // answered a prior turn with a selection that no longer exists — for
        // example because the goal changed or authorization filtered the
        // candidate out. Surface the same invalid_argument error the "id not
        // in ranking" path raises instead of silently ignoring the choice.
        var matchIndex = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].Id, pinnedId, StringComparison.Ordinal))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
        {
            throw new GeoprocessingValidationException(
                $"Clarification answer for '{questionId}' references '{pinnedId}', "
                + "which is not present in the current ranking. Re-run grounding to see the updated choices.");
        }

        appliedIds.Add(questionId);

        if (matchIndex == 0)
        {
            return candidates;
        }

        var reordered = new List<GroundingCandidate>(candidates.Count) { candidates[matchIndex] };
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i != matchIndex)
            {
                reordered.Add(candidates[i]);
            }
        }
        return reordered;
    }

    private static string? FirstNonBlank(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                return values[i].Trim();
            }
        }
        return null;
    }

    private static TEnum ParseEnumOrThrow<TEnum>(string questionId, string value, string label)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new GeoprocessingValidationException(
                $"Clarification answer for '{questionId}' value '{value}' is not a recognised {label}. "
                + $"Expected one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        }
        return parsed;
    }
}
