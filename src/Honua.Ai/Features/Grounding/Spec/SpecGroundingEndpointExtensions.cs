// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Grounding.Spec;

internal static class SpecGroundingEndpointExtensions
{
    private const string RoutePrefix = "/v1/grounding/spec";
    private const string JsonMimeType = "application/json";

    public static IEndpointRouteBuilder MapSpecGroundingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{RoutePrefix}/mutate", HandleMutateAsync)
            .WithDisplayName("Spec Grounding Mutate")
            .WithName("SpecGroundingMutate")
            .WithSummary("Grounds a natural-language turn into a validated spec mutation plan.")
            .WithTags("Grounding", "Spec")
            .RequireAdminAuthorization();

        endpoints.MapPost($"{RoutePrefix}/summarize", HandleSummarizeAsync)
            .WithDisplayName("Spec Grounding Summarize")
            .WithName("SpecGroundingSummarize")
            .WithSummary("Produces deterministic section summaries for a canonical spec.")
            .WithTags("Grounding", "Spec")
            .RequireAdminAuthorization();

        return endpoints;
    }

    private static async Task<IResult> HandleMutateAsync(
        HttpContext httpContext,
        SpecGroundingService groundingService,
        CancellationToken cancellationToken)
    {
        var request = await httpContext.Request.ReadFromJsonAsync(
                SpecGroundingJsonContext.Default.SpecMutateRequestDto,
                cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return InvalidRequest("Request body must be a JSON object.");
        }

        if (string.IsNullOrWhiteSpace(request.Turn))
        {
            return InvalidRequest("turn is required.");
        }

        if (!TryReadSpecDocument(request.Spec, out var currentSpec, out var specError))
        {
            return InvalidRequest(specError);
        }

        if (!TryMapClarificationAnswer(request.ClarificationAnswer, out var clarificationAnswer, out var clarificationError))
        {
            return InvalidRequest(clarificationError);
        }

        var result = await groundingService.MutateAsync(
                currentSpec,
                request.Turn,
                ToDomain(request.Context),
                clarificationAnswer,
                httpContext.User,
                cancellationToken)
            .ConfigureAwait(false);

        var response = ToMutateResponse(result);
        return Results.Json(
            response,
            SpecGroundingJsonContext.Default.SpecMutateResponseDto,
            contentType: JsonMimeType);
    }

    private static async Task<IResult> HandleSummarizeAsync(
        HttpContext httpContext,
        SpecGroundingService groundingService,
        CancellationToken cancellationToken)
    {
        var request = await httpContext.Request.ReadFromJsonAsync(
                SpecGroundingJsonContext.Default.SpecSummarizeRequestDto,
                cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return InvalidRequest("Request body must be a JSON object.");
        }

        if (!TryReadSpecDocument(request.Spec, out var document, out var specError))
        {
            return InvalidRequest(specError);
        }

        var summary = groundingService.Summarize(document);
        var response = new SpecSummarizeResponseDto
        {
            TitleSummary = summary.TitleSummary,
            SectionSummaries = summary.Sections
                .Select(section => new SpecSectionSummaryDto
                {
                    SectionId = section.SectionId,
                    Text = section.Text
                })
                .ToList()
        };

        return Results.Json(
            response,
            SpecGroundingJsonContext.Default.SpecSummarizeResponseDto,
            contentType: JsonMimeType);
    }

    private static SpecMutateResponseDto ToMutateResponse(SpecGroundingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SpecMutateResponseDto
        {
            Mutation = result.Mutation is null ? null : ToMutationPlan(result.Mutation),
            Clarifications = result.Clarification is null ? [] : ToClarifications(result.Clarification),
            Warnings = result.Warnings
                .Where(diagnostic => diagnostic.Severity != SpecDiagnosticSeverity.Error)
                .Select(ToDiagnostic)
                .ToList(),
            Error = result.Clarification is not null || result.ErrorKind is null
                ? null
                : new SpecGroundingErrorDto
                {
                    Kind = ToErrorKind(result.ErrorKind.Value),
                    Message = result.ErrorMessage ?? "Spec grounding failed."
                }
        };
    }

    private static SpecMutationPlanDto ToMutationPlan(SpecMutationPlan plan)
        => new()
        {
            Mutations = plan.Mutations.Select(ToMutation).ToList(),
            NextSpec = ParseJsonElement(plan.NextSpecCanonicalJson),
            SectionsTouched = plan.SectionsTouched.ToList(),
            SectionsPreserved = plan.SectionsPreserved.ToList()
        };

    private static SpecMutationDto ToMutation(SpecMutation mutation)
        => mutation switch
        {
            AddSourceMutation addSource => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                SourceId = addSource.SourceId,
                SourceType = addSource.SourceType,
                SourceRef = addSource.SourceRef
            },
            RemoveSourceMutation removeSource => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                SourceId = removeSource.SourceId
            },
            AddScopeClauseMutation addScope => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                TargetId = addScope.TargetId,
                Predicate = addScope.Predicate
            },
            AddComputeMutation addCompute => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                ComputeId = addCompute.ComputeId,
                Operator = addCompute.OperatorName,
                Inputs = addCompute.Inputs.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                Parameters = addCompute.Parameters?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            },
            RemoveComputeMutation removeCompute => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                ComputeId = removeCompute.ComputeId
            },
            SetMapLayerMutation setMapLayer => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                LayerIds = setMapLayer.LayerIds.ToList()
            },
            SetViewportMutation setViewport => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                Viewport = setViewport.Values.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            },
            SetOutputMutation setOutput => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                OutputId = setOutput.OutputId,
                Expression = setOutput.Expression
            },
            RenameReferenceMutation rename => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind),
                FromId = rename.FromId,
                ToId = rename.ToId
            },
            _ => new SpecMutationDto
            {
                Kind = ToMutationKind(mutation.Kind)
            }
        };

    private static List<SpecClarificationDto> ToClarifications(SpecClarificationEnvelope clarification)
    {
        var reasonCodes = clarification.Request.ReasonCodes
            .Select(ToReasonCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return clarification.Request.Questions
            .Select(question => new SpecClarificationDto
            {
                IntentId = clarification.Request.IntentId,
                Kind = ToClarificationKind(question.QuestionId),
                ReasonCodes = reasonCodes,
                QuestionId = question.QuestionId,
                QuestionKind = ToQuestionKind(question.Kind),
                Prompt = question.Prompt,
                Candidates = clarification.CandidatesByQuestionId.TryGetValue(question.QuestionId, out var candidates)
                    ? candidates.Select(ToClarificationCandidate).ToList()
                    : []
            })
            .ToList();
    }

    private static SpecClarificationCandidateDto ToClarificationCandidate(SpecClarificationCandidate candidate)
        => new()
        {
            CandidateType = candidate.CandidateType,
            Id = candidate.Id,
            Label = candidate.Label,
            CatalogRef = candidate.CatalogRef,
            SchemaPreview = candidate.SchemaPreview?.ToList(),
            ColumnName = candidate.ColumnName,
            TypeRef = candidate.TypeRef,
            Nullable = candidate.Nullable,
            Sample = candidate.Sample,
            Value = candidate.Value,
            Count = candidate.Count,
            Unit = candidate.Unit,
            Crs = candidate.Crs,
            OperatorName = candidate.OperatorName
        };

    private static SpecDiagnosticDto ToDiagnostic(SpecDiagnostic diagnostic)
        => new()
        {
            Code = diagnostic.Code.ToString(),
            Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            Message = diagnostic.Message
        };

    private static SpecGroundingContext? ToDomain(SpecGroundingContextDto? context)
        => context is null
            ? null
            : new SpecGroundingContext(
                context.TargetId,
                context.DefaultCrs,
                context.DefaultUnit,
                context.Hints);

    private static bool TryMapClarificationAnswer(
        SpecClarificationAnswerDto? answer,
        out ClarificationResponse? clarificationResponse,
        out string error)
    {
        if (answer is null)
        {
            clarificationResponse = null;
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(answer.IntentId))
        {
            clarificationResponse = null;
            error = "clarification_answer.intent_id is required when clarification_answer is supplied.";
            return false;
        }

        if (answer.Answers is not { Count: > 0 })
        {
            clarificationResponse = null;
            error = "clarification_answer.answers must contain at least one answer.";
            return false;
        }

        var mappedAnswers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in answer.Answers)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                clarificationResponse = null;
                error = "clarification_answer.answers cannot contain a blank question id.";
                return false;
            }

            var values = entry.Value?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (values is not { Length: > 0 })
            {
                clarificationResponse = null;
                error = $"clarification_answer.answers['{entry.Key}'] must contain at least one non-blank value.";
                return false;
            }

            mappedAnswers[entry.Key] = values;
        }

        clarificationResponse = new ClarificationResponse
        {
            IntentId = answer.IntentId,
            Answers = mappedAnswers
        };
        error = string.Empty;
        return true;
    }

    private static bool TryReadSpecDocument(
        JsonElement spec,
        out SpecDocument document,
        out string error)
    {
        if (spec.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            document = CreateEmptySpecDocument();
            error = "spec is required.";
            return false;
        }

        if (spec.ValueKind != JsonValueKind.Object)
        {
            document = CreateEmptySpecDocument();
            error = "spec must be a canonical JSON object.";
            return false;
        }

        try
        {
            document = spec.EnumerateObject().Any()
                ? SpecJsonReader.Read(spec.GetRawText())
                : CreateEmptySpecDocument();
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            document = CreateEmptySpecDocument();
            error = $"spec is not valid canonical JSON: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            document = CreateEmptySpecDocument();
            error = $"spec could not be read: {ex.Message}";
            return false;
        }
        catch (FormatException ex)
        {
            document = CreateEmptySpecDocument();
            error = $"spec could not be read: {ex.Message}";
            return false;
        }
    }

    private static SpecDocument CreateEmptySpecDocument()
        => new(
            SourceSpan.Synthetic,
            SpecGrammarVersion.Current,
            SourceSpan.Synthetic,
            "analysis",
            null,
            ImmutableArray<Honua.Core.Features.Spec.Domain.SourceBinding>.Empty,
            ImmutableArray<ScopeClause>.Empty,
            ImmutableArray<ComputeStep>.Empty,
            null,
            ImmutableArray<OutputBinding>.Empty,
            ImmutableDictionary<string, string>.Empty);

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IResult InvalidRequest(string detail)
        => Results.Problem(
            title: "Invalid spec grounding request",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);

    private static string ToClarificationKind(string questionId) => questionId switch
    {
        "dataset.selection" => "pick-dataset",
        "column.selection" => "pick-column",
        "value.selection" => "pick-value",
        "unit.selection" => "specify-unit",
        "crs.selection" => "specify-crs",
        "operator.selection" => "choose-op",
        "heavy.confirm" => "confirm-heavy-op",
        _ => questionId
    };

    private static string ToQuestionKind(ClarificationQuestionKind kind) => kind switch
    {
        ClarificationQuestionKind.SingleSelect => "single-select",
        ClarificationQuestionKind.MultiSelect => "multi-select",
        ClarificationQuestionKind.FreeText => "free-text",
        ClarificationQuestionKind.Confirmation => "confirmation",
        _ => kind.ToString()
    };

    private static string ToReasonCode(ClarificationReasonCode reasonCode) => reasonCode switch
    {
        ClarificationReasonCode.AmbiguousDataset => "ambiguous_dataset",
        ClarificationReasonCode.AmbiguousColumn => "ambiguous_column",
        ClarificationReasonCode.AmbiguousFilterValue => "ambiguous_filter_value",
        ClarificationReasonCode.AmbiguousUnit => "ambiguous_unit",
        ClarificationReasonCode.AmbiguousCrs => "ambiguous_crs",
        ClarificationReasonCode.AmbiguousProcess => "ambiguous_process",
        ClarificationReasonCode.HeavyOperationConfirmation => "heavy_operation_confirmation",
        _ => reasonCode.ToString()
    };

    private static string ToErrorKind(SpecGroundingErrorKind errorKind) => errorKind switch
    {
        SpecGroundingErrorKind.Unresolvable => "unresolvable",
        SpecGroundingErrorKind.Ambiguous => "ambiguous",
        SpecGroundingErrorKind.InvalidMutation => "invalid_mutation",
        SpecGroundingErrorKind.OutOfScope => "out_of_scope",
        _ => errorKind.ToString()
    };

    private static string ToMutationKind(SpecMutationKind kind) => kind switch
    {
        SpecMutationKind.AddSource => "add-source",
        SpecMutationKind.RemoveSource => "remove-source",
        SpecMutationKind.AddScopeClause => "add-scope-clause",
        SpecMutationKind.AddCompute => "add-compute",
        SpecMutationKind.RemoveCompute => "remove-compute",
        SpecMutationKind.SetMapLayer => "set-map-layer",
        SpecMutationKind.SetViewport => "set-viewport",
        SpecMutationKind.SetOutput => "set-output",
        SpecMutationKind.RenameReference => "rename-reference",
        _ => kind.ToString()
    };
}
