// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Grounding.Spec;

internal sealed partial class SpecGroundingService
{
    private static readonly string[] CrsCandidates = ["EPSG:3857", "EPSG:32604", "EPSG:26910"];
    private static readonly string[] UnitCandidates = ["km", "m", "mi", "ft", "nm"];
    private const string RoadmapPointer = "See docs/developer/spec-grammar/v1.0/README.md for the supported S1 scope.";

    private readonly ISpecCanonicalizer _canonicalizer;
    private readonly ISpecValidator _validator;
    private readonly SpecMutationApplier _applier;
    private readonly SpecSummarizer _summarizer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpecGroundingService> _logger;

    public SpecGroundingService(
        ISpecCanonicalizer canonicalizer,
        ISpecValidator validator,
        SpecMutationApplier applier,
        SpecSummarizer summarizer,
        IServiceScopeFactory scopeFactory,
        ILogger<SpecGroundingService> logger)
    {
        _canonicalizer = canonicalizer;
        _validator = validator;
        _applier = applier;
        _summarizer = summarizer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<SpecGroundingResult> MutateAsync(
        SpecDocument currentSpec,
        string turn,
        SpecGroundingContext? context,
        ClarificationResponse? clarificationAnswer,
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn);

        var retried = clarificationAnswer is not null;
        SpecGroundingLog.MutateStarted(_logger, turn.Length, retried);

        var warnings = new List<SpecDiagnostic>();
        var layers = await LoadLayersAsync(warnings, cancellationToken).ConfigureAwait(false);
        var catalogSnapshot = layers.Count == 0
            ? StaticSpecCatalogSnapshot.Empty
            : new LayerCatalogSpecCatalogSnapshot(layers);

        var inputDiagnostics = _validator.Validate(currentSpec, catalogSnapshot).Diagnostics;
        warnings.AddRange(inputDiagnostics.Where(diagnostic => diagnostic.Severity != SpecDiagnosticSeverity.Error));
        if (HasError(inputDiagnostics))
        {
            var errors = inputDiagnostics.Where(diagnostic => diagnostic.Severity == SpecDiagnosticSeverity.Error).ToArray();
            SpecGroundingTelemetry.RecordValidationFailures(errors.Select(diagnostic => diagnostic.Code.ToString()));
            SpecGroundingTelemetry.RecordMutateTurn(clarified: false, retried: retried, errorKind: "unresolvable");
            SpecGroundingLog.MutateRejected(_logger, "unresolvable", "The input spec is not valid.");
            return new SpecGroundingResult
            {
                ErrorKind = SpecGroundingErrorKind.Unresolvable,
                ErrorMessage = "The input spec is not valid.",
                Warnings = warnings.Concat(errors).ToArray()
            };
        }

        var normalizedContext = context ?? new SpecGroundingContext();
        var workingDocument = currentSpec;
        var mutations = new List<SpecMutation>();
        var handledAnyClause = false;

        foreach (var clause in SplitClauses(turn))
        {
            if (IsOutOfScope(clause))
            {
                var message = $"Turn '{clause}' is outside the supported spec-grounding scope. {RoadmapPointer}";
                SpecGroundingTelemetry.RecordMutateTurn(clarified: false, retried: retried, errorKind: "out_of_scope");
                SpecGroundingLog.MutateRejected(_logger, "out_of_scope", message);
                warnings.Add(SpecDiagnostic.Warning(
                    SpecDiagnosticCode.UnknownOperator,
                    RoadmapPointer,
                    SourceSpan.Synthetic));
                return new SpecGroundingResult
                {
                    ErrorKind = SpecGroundingErrorKind.OutOfScope,
                    ErrorMessage = message,
                    Warnings = warnings
                };
            }

            var clauseResult = await PlanClauseAsync(
                workingDocument,
                clause,
                normalizedContext,
                clarificationAnswer,
                layers,
                cancellationToken).ConfigureAwait(false);

            warnings.AddRange(clauseResult.Warnings);

            if (clauseResult.Clarification is not null)
            {
                SpecGroundingTelemetry.RecordMutateTurn(clarified: true, retried: retried, errorKind: "ambiguous");
                SpecGroundingLog.MutateCompleted(_logger, mutations.Count, clauseResult.Clarification.Request.Questions.Count, "ambiguous");
                return new SpecGroundingResult
                {
                    Clarification = clauseResult.Clarification,
                    ErrorKind = SpecGroundingErrorKind.Ambiguous,
                    ErrorMessage = "Clarification is required before the turn can be applied.",
                    Warnings = warnings
                };
            }

            if (clauseResult.ErrorKind is not null)
            {
                SpecGroundingTelemetry.RecordMutateTurn(
                    clarified: false,
                    retried: retried,
                    errorKind: clauseResult.ErrorKind.Value.ToString().ToLowerInvariant());
                SpecGroundingLog.MutateRejected(
                    _logger,
                    clauseResult.ErrorKind.Value.ToString().ToLowerInvariant(),
                    clauseResult.ErrorMessage ?? "Spec grounding rejected.");
                return new SpecGroundingResult
                {
                    ErrorKind = clauseResult.ErrorKind,
                    ErrorMessage = clauseResult.ErrorMessage,
                    Warnings = warnings
                };
            }

            if (clauseResult.Mutations.Count == 0)
            {
                continue;
            }

            handledAnyClause = true;
            mutations.AddRange(clauseResult.Mutations);
            workingDocument = _applier.Apply(workingDocument, clauseResult.Mutations);
        }

        if (!handledAnyClause || mutations.Count == 0)
        {
            const string message = "The turn could not be grounded to a supported spec mutation.";
            SpecGroundingTelemetry.RecordMutateTurn(clarified: false, retried: retried, errorKind: "unresolvable");
            SpecGroundingLog.MutateRejected(_logger, "unresolvable", message);
            return new SpecGroundingResult
            {
                ErrorKind = SpecGroundingErrorKind.Unresolvable,
                ErrorMessage = message,
                Warnings = warnings
            };
        }

        SpecDocument nextDocument;
        try
        {
            nextDocument = _applier.Apply(currentSpec, mutations);
        }
        catch (InvalidOperationException ex)
        {
            warnings.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.UnknownReference,
                ex.Message,
                SourceSpan.Synthetic));
            SpecGroundingTelemetry.RecordValidationFailures([SpecDiagnosticCode.UnknownReference.ToString()]);
            SpecGroundingTelemetry.RecordMutateTurn(clarified: false, retried: retried, errorKind: "invalid_mutation");
            SpecGroundingLog.MutateRejected(_logger, "invalid_mutation", ex.Message);
            return new SpecGroundingResult
            {
                ErrorKind = SpecGroundingErrorKind.InvalidMutation,
                ErrorMessage = ex.Message,
                Warnings = warnings
            };
        }

        var outputDiagnostics = _validator.Validate(nextDocument, catalogSnapshot).Diagnostics;
        warnings.AddRange(outputDiagnostics.Where(diagnostic => diagnostic.Severity != SpecDiagnosticSeverity.Error));
        var outputErrors = outputDiagnostics.Where(diagnostic => diagnostic.Severity == SpecDiagnosticSeverity.Error).ToArray();
        if (outputErrors.Length > 0)
        {
            SpecGroundingTelemetry.RecordValidationFailures(outputErrors.Select(diagnostic => diagnostic.Code.ToString()));
            SpecGroundingTelemetry.RecordMutateTurn(clarified: false, retried: retried, errorKind: "invalid_mutation");
            SpecGroundingLog.MutateRejected(_logger, "invalid_mutation", outputErrors[0].Message);
            return new SpecGroundingResult
            {
                ErrorKind = SpecGroundingErrorKind.InvalidMutation,
                ErrorMessage = "The proposed mutation did not validate against the spec grammar.",
                Warnings = warnings.Concat(outputErrors).ToArray()
            };
        }

        var canonicalJson = _canonicalizer.ToJson(nextDocument);
        var renameTouchedSections = mutations.Any(static mutation => mutation.Kind == SpecMutationKind.RenameReference)
            ? DetermineChangedSections(_canonicalizer.ToJson(currentSpec), canonicalJson)
            : null;
        var touchedSections = DetermineTouchedSections(mutations, renameTouchedSections);
        var preservedSections = AllSections().Except(touchedSections, StringComparer.Ordinal).ToArray();

        SpecGroundingTelemetry.RecordMutationKinds(mutations, touchedSections);
        SpecGroundingTelemetry.RecordMutateTurn(clarified: false, retried: retried, errorKind: null);
        SpecGroundingLog.MutateCompleted(_logger, mutations.Count, 0, null);

        return new SpecGroundingResult
        {
            Mutation = new SpecMutationPlan(
                mutations.ToArray(),
                nextDocument,
                canonicalJson,
                touchedSections,
                preservedSections),
            Warnings = warnings
        };
    }

    public SpecSummary Summarize(SpecDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var started = Stopwatch.GetTimestamp();
        var summary = _summarizer.Summarize(document);
        var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        SpecGroundingTelemetry.RecordSummary(summary.Sections.Count, durationMs);
        SpecGroundingLog.SummarizeCompleted(_logger, summary.Sections.Count, durationMs);
        return summary;
    }

    private async Task<ClausePlanResult> PlanClauseAsync(
        SpecDocument document,
        string clause,
        SpecGroundingContext context,
        ClarificationResponse? clarificationAnswer,
        IReadOnlyList<LayerDefinition> layers,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var lower = clause.ToLowerInvariant();

        if (TryParseRename(lower, out var rename))
        {
            if (!HasLocal(document, rename.FromId))
            {
                return ClausePlanResult.Error(
                    SpecGroundingErrorKind.Unresolvable,
                    $"Rename target '{rename.FromId}' does not exist.");
            }

            return ClausePlanResult.FromMutation(new RenameReferenceMutation(rename.FromId, rename.ToId));
        }

        if (TryParseRemoveSource(lower, out var removeSource))
        {
            return ClausePlanResult.FromMutation(new RemoveSourceMutation(removeSource));
        }

        if (TryParseRemoveCompute(lower, out var removeCompute))
        {
            return ClausePlanResult.FromMutation(new RemoveComputeMutation(removeCompute));
        }

        if (TryParseSummarySource(clause, out var sourceSummary))
        {
            return await ResolveSourceClauseAsync(
                document,
                sourceSummary.SourcePhrase,
                sourceSummary.SourceId,
                layers,
                clarificationAnswer).ConfigureAwait(false);
        }

        if (TryParseUseDataset(clause, out var useDataset))
        {
            return await ResolveSourceClauseAsync(
                document,
                useDataset.SourcePhrase,
                useDataset.SourceId,
                layers,
                clarificationAnswer).ConfigureAwait(false);
        }

        if (TryParseSummaryScope(clause, out var summaryScope))
        {
            if (!HasLocal(document, summaryScope.TargetId))
            {
                return ClausePlanResult.Error(
                    SpecGroundingErrorKind.Unresolvable,
                    $"Scope target '{summaryScope.TargetId}' does not exist.");
            }

            return ClausePlanResult.FromMutation(
                new AddScopeClauseMutation(summaryScope.TargetId, summaryScope.Predicate));
        }

        if (TryParseOnlyClause(clause, out var onlyTail))
        {
            return ResolveOnlyClause(document, onlyTail, context, layers, clarificationAnswer);
        }

        if (TryParseSummaryCompute(clause, out var summaryCompute))
        {
            if (RequiresHeavyConfirmation(summaryCompute.OperatorName))
            {
                var clarification = CreateHeavyConfirmation(summaryCompute.OperatorName);
                if (!IsConfirmed(clarificationAnswer, clarification.Request.IntentId, "heavy.confirm"))
                {
                    return ClausePlanResult.FromClarification(clarification);
                }
            }

            return ClausePlanResult.FromMutation(new AddComputeMutation(
                summaryCompute.ComputeId,
                summaryCompute.OperatorName,
                summaryCompute.Inputs,
                summaryCompute.Parameters));
        }

        if (TryParseBuffer(clause, out var buffer))
        {
            if (!TryResolveTargetReference(document, buffer.Target, out var targetId))
            {
                return ClausePlanResult.Error(
                    SpecGroundingErrorKind.Unresolvable,
                    $"Buffer target '{buffer.Target}' does not match any known source or compute id.");
            }

            var unit = buffer.Unit ?? context.DefaultUnit;
            if (string.IsNullOrWhiteSpace(unit))
            {
                var clarification = CreateUnitClarification();
                unit = GetSingleAnswer(clarificationAnswer, clarification.Request.IntentId, "unit.selection");
                if (string.IsNullOrWhiteSpace(unit))
                {
                    return ClausePlanResult.FromClarification(clarification);
                }
            }

            var crs = buffer.Crs ?? context.DefaultCrs;
            if (string.IsNullOrWhiteSpace(crs))
            {
                var clarification = CreateCrsClarification();
                crs = GetSingleAnswer(clarificationAnswer, clarification.Request.IntentId, "crs.selection");
                if (string.IsNullOrWhiteSpace(crs))
                {
                    return ClausePlanResult.FromClarification(clarification);
                }
            }

            var computeId = buffer.ComputeId ?? NormalizeIdentifier($"{targetId}_buffer");
            return ClausePlanResult.FromMutation(new AddComputeMutation(
                computeId,
                "buffer",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = $"@{targetId}" },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["distance"] = $"{buffer.Distance}.{unit}",
                    ["crs"] = crs
                }));
        }

        if (TryParseWithin(clause, out var within))
        {
            var unit = within.Unit ?? context.DefaultUnit;
            if (string.IsNullOrWhiteSpace(unit))
            {
                var clarification = CreateUnitClarification();
                unit = GetSingleAnswer(clarificationAnswer, clarification.Request.IntentId, "unit.selection");
                if (string.IsNullOrWhiteSpace(unit))
                {
                    return ClausePlanResult.FromClarification(clarification);
                }
            }

            var crs = within.Crs ?? context.DefaultCrs;
            if (string.IsNullOrWhiteSpace(crs))
            {
                var clarification = CreateCrsClarification();
                crs = GetSingleAnswer(clarificationAnswer, clarification.Request.IntentId, "crs.selection");
                if (string.IsNullOrWhiteSpace(crs))
                {
                    return ClausePlanResult.FromClarification(clarification);
                }
            }

            var leftResolution = await ResolveSourceReferenceAsync(
                document,
                within.LeftPhrase,
                sourceIdHint: null,
                layers,
                clarificationAnswer).ConfigureAwait(false);
            if (!leftResolution.IsSuccess)
            {
                return leftResolution.ToClauseResult();
            }

            var leftMutations = new List<SpecMutation>();
            if (leftResolution.AddSourceMutation is not null)
            {
                leftMutations.Add(leftResolution.AddSourceMutation);
                document = _applier.Apply(document, leftMutations);
            }

            var rightResolution = await ResolveSourceReferenceAsync(
                document,
                within.RightPhrase,
                sourceIdHint: null,
                layers,
                clarificationAnswer).ConfigureAwait(false);
            if (!rightResolution.IsSuccess)
            {
                return rightResolution.ToClauseResult();
            }

            var mutations = new List<SpecMutation>(capacity: 4);
            if (leftResolution.AddSourceMutation is not null)
            {
                mutations.Add(leftResolution.AddSourceMutation);
            }

            if (rightResolution.AddSourceMutation is not null)
            {
                mutations.Add(rightResolution.AddSourceMutation);
            }

            var bufferId = NormalizeIdentifier($"{rightResolution.SourceId}_buffer");
            mutations.Add(new AddComputeMutation(
                bufferId,
                "buffer",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["input"] = $"@{rightResolution.SourceId}"
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["distance"] = $"{within.Distance}.{unit}",
                    ["crs"] = crs
                }));

            var joinId = NormalizeIdentifier($"{leftResolution.SourceId}_within_{rightResolution.SourceId}");
            mutations.Add(new AddComputeMutation(
                joinId,
                "spatial_join",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["left"] = $"@{leftResolution.SourceId}",
                    ["right"] = $"@{bufferId}"
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["crs"] = crs
                }));

            return ClausePlanResult.FromMutations(mutations);
        }

        if (ContainsNearAmbiguity(lower) && !HasDirectOperator(lower))
        {
            var clarification = CreateOperatorClarification();
            var answer = GetSingleAnswer(clarificationAnswer, clarification.Request.IntentId, "operator.selection");
            if (string.IsNullOrWhiteSpace(answer))
            {
                return ClausePlanResult.FromClarification(clarification);
            }
        }

        if (RequiresHeavyConfirmation(lower))
        {
            var clarification = CreateHeavyConfirmation("zonal_stats");
            if (!IsConfirmed(clarificationAnswer, clarification.Request.IntentId, "heavy.confirm"))
            {
                return ClausePlanResult.FromClarification(clarification);
            }
        }

        if (TryParseShowMap(clause, out var map))
        {
            var layerIds = map
                .Where(id => TryResolveTargetReference(document, id, out _))
                .Select(id => id.Trim().TrimStart('@'))
                .ToList();
            if (layerIds.Count == 0)
            {
                return ClausePlanResult.Error(
                    SpecGroundingErrorKind.Unresolvable,
                    "The requested map layer does not match any known source, compute, or output id.");
            }

            return ClausePlanResult.FromMutation(new SetMapLayerMutation(layerIds));
        }

        if (TryParseViewport(clause, out var viewport))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["center"] = $"{viewport.Longitude},{viewport.Latitude}"
            };
            values["zoom"] = viewport.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var viewportFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["zoom"] = viewport.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            return ClausePlanResult.FromMutation(new SetViewportMutation(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["center"] = $"{viewport.Longitude},{viewport.Latitude}",
                ["zoom"] = viewport.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        }

        if (TryParseOutput(clause, out var output))
        {
            if (!TryResolveTargetReference(document, output.Expression, out var resolvedExpression))
            {
                return ClausePlanResult.Error(
                    SpecGroundingErrorKind.Unresolvable,
                    $"Output expression '{output.Expression}' does not match any known source or compute id.");
            }

            return ClausePlanResult.FromMutation(
                new SetOutputMutation(output.OutputId, $"@{resolvedExpression}"));
        }

        if (TryParseReproject(clause, out var reproject))
        {
            if (!TryResolveTargetReference(document, reproject.Target, out var targetId))
            {
                return ClausePlanResult.Error(
                    SpecGroundingErrorKind.Unresolvable,
                    $"Reproject target '{reproject.Target}' does not match any known source or compute id.");
            }

            var crs = reproject.Crs ?? context.DefaultCrs;
            if (string.IsNullOrWhiteSpace(crs))
            {
                var clarification = CreateCrsClarification();
                crs = GetSingleAnswer(clarificationAnswer, clarification.Request.IntentId, "crs.selection");
                if (string.IsNullOrWhiteSpace(crs))
                {
                    return ClausePlanResult.FromClarification(clarification);
                }
            }

            var computeId = reproject.ComputeId ?? NormalizeIdentifier($"{targetId}_reproject");
            return ClausePlanResult.FromMutation(new AddComputeMutation(
                computeId,
                "reproject",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = $"@{targetId}" },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["crs"] = crs }));
        }

        return ClausePlanResult.None();
    }

    private ClausePlanResult ResolveOnlyClause(
        SpecDocument document,
        string tail,
        SpecGroundingContext context,
        IReadOnlyList<LayerDefinition> layers,
        ClarificationResponse? clarificationAnswer)
    {
        var targetId = ResolveScopeTarget(document, context, tail);
        if (targetId is null)
        {
            return ClausePlanResult.Error(
                SpecGroundingErrorKind.Unresolvable,
                "The filter turn does not identify which source should be narrowed.");
        }

        var layer = TryResolveLayerForSource(document, layers, targetId);
        if (layer is null)
        {
            return ClausePlanResult.Error(
                SpecGroundingErrorKind.Unresolvable,
                $"Source '{targetId}' does not resolve to a known catalog layer.");
        }

        var stringFields = layer.Fields
            .Where(field => field.Type == FieldType.String)
            .ToArray();

        if (stringFields.Length == 0)
        {
            return ClausePlanResult.Error(
                SpecGroundingErrorKind.Unresolvable,
                $"Layer '{layer.Name}' does not expose a string field that can be filtered.");
        }

        var fieldCandidates = ResolveFieldCandidates(stringFields, tail);
        var parts = tail.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var valueCandidates = parts
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? resolvedValue;
        if (valueCandidates.Length > 1)
        {
            var clarification = CreateValueClarification(valueCandidates);
            if (!TryGetSelectedValue(
                    clarificationAnswer,
                    clarification.Request.IntentId,
                    valueCandidates,
                    out var selectedValue))
            {
                return ClausePlanResult.FromClarification(clarification);
            }

            resolvedValue = selectedValue;
        }
        else
        {
            resolvedValue = valueCandidates.Length > 0 ? valueCandidates[0] : tail.Trim();
        }

        FieldDefinition? field;
        if (fieldCandidates.Length > 1)
        {
            var clarification = CreateColumnClarification(fieldCandidates);
            if (!TryGetSelectedField(
                    clarificationAnswer,
                    clarification.Request.IntentId,
                    fieldCandidates,
                    out var selectedField))
            {
                return ClausePlanResult.FromClarification(clarification);
            }

            field = selectedField;
        }
        else if (fieldCandidates.Length == 1)
        {
            field = fieldCandidates[0];
        }
        else if (stringFields.Length == 1)
        {
            field = stringFields[0];
        }
        else
        {
            var clarification = CreateColumnClarification(stringFields);
            if (!TryGetSelectedField(
                    clarificationAnswer,
                    clarification.Request.IntentId,
                    stringFields,
                    out var selectedFallbackField))
            {
                return ClausePlanResult.FromClarification(clarification);
            }

            field = selectedFallbackField;
        }

        var value = TrimKnownFieldSuffix(resolvedValue, field.Name);
        var predicate = $"{field.Name} = '{value.Replace("'", "''", StringComparison.Ordinal)}'";
        return ClausePlanResult.FromMutation(new AddScopeClauseMutation(targetId, predicate));
    }

    private async Task<ClausePlanResult> ResolveSourceClauseAsync(
        SpecDocument document,
        string sourcePhrase,
        string? sourceIdHint,
        IReadOnlyList<LayerDefinition> layers,
        ClarificationResponse? clarificationAnswer)
    {
        var resolution = await ResolveSourceReferenceAsync(
            document,
            sourcePhrase,
            sourceIdHint,
            layers,
            clarificationAnswer).ConfigureAwait(false);

        return resolution.ToClauseResult();
    }

    private async Task<SourceResolution> ResolveSourceReferenceAsync(
        SpecDocument document,
        string sourcePhrase,
        string? sourceIdHint,
        IReadOnlyList<LayerDefinition> layers,
        ClarificationResponse? clarificationAnswer)
    {
        var existing = TryMatchExistingSource(document, sourcePhrase);
        if (existing is not null)
        {
            return SourceResolution.Success(existing.Id, addSourceMutation: null);
        }

        if (layers.Count == 0)
        {
            return SourceResolution.Error(
                SpecGroundingErrorKind.Unresolvable,
                $"No catalog layers are available to resolve '{sourcePhrase}'.");
        }

        var candidates = ResolveLayerCandidates(layers, sourcePhrase).ToArray();
        if (candidates.Length == 0)
        {
            return SourceResolution.Error(
                SpecGroundingErrorKind.Unresolvable,
                $"No catalog dataset matched '{sourcePhrase}'.");
        }

        if (candidates.Length > 1)
        {
            var clarification = CreateDatasetClarification(candidates);
            if (!TryGetSelectedLayer(
                    clarificationAnswer,
                    clarification.Request.IntentId,
                    candidates,
                    out var selectedLayer))
            {
                return SourceResolution.FromClarification(clarification);
            }

            var selectedSourceId = ResolveSourceId(document, sourceIdHint ?? NormalizeIdentifier(selectedLayer.Name));
            return SourceResolution.Success(
                selectedSourceId,
                new AddSourceMutation(
                    selectedSourceId,
                    "layer",
                    $"catalog:layer:{selectedLayer.Id}",
                    selectedLayer.Name));
        }

        var layer = candidates[0].Layer;
        var sourceId = ResolveSourceId(document, sourceIdHint ?? NormalizeIdentifier(layer.Name));
        return SourceResolution.Success(
            sourceId,
            new AddSourceMutation(sourceId, "layer", $"catalog:layer:{layer.Id}", layer.Name));
    }

    private async Task<IReadOnlyList<LayerDefinition>> LoadLayersAsync(
        List<SpecDiagnostic> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var catalog = scope.ServiceProvider.GetService<ILayerCatalog>();
            if (catalog is null)
            {
                return [];
            }

            return await catalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SpecGroundingLog.CatalogUnavailable(_logger, ex.Message);
            warnings.Add(SpecDiagnostic.Warning(
                SpecDiagnosticCode.CatalogUnavailable,
                $"Layer catalog unavailable: {ex.Message}",
                SourceSpan.Synthetic));
            return [];
        }
    }

    private static IEnumerable<string> SplitClauses(string turn)
    {
        var sentences = SentenceBreakPattern().Split(turn);
        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed.TrimEnd('.');
            }
        }
    }

    private static bool IsOutOfScope(string clause)
        => clause.Contains("schedule", StringComparison.OrdinalIgnoreCase) ||
           clause.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
           clause.Contains("deploy", StringComparison.OrdinalIgnoreCase) ||
           clause.Contains("dashboard", StringComparison.OrdinalIgnoreCase) ||
           clause.Contains("app", StringComparison.OrdinalIgnoreCase);

    private static bool HasError(IEnumerable<SpecDiagnostic> diagnostics)
        => diagnostics.Any(diagnostic => diagnostic.Severity == SpecDiagnosticSeverity.Error);

    private static bool HasLocal(SpecDocument document, string id)
        => document.Sources.Any(source => string.Equals(source.Id, id, StringComparison.Ordinal)) ||
           document.Compute.Any(step => string.Equals(step.Id, id, StringComparison.Ordinal)) ||
           document.Outputs.Any(output => string.Equals(output.Id, id, StringComparison.Ordinal));

    private static string[] DetermineTouchedSections(
        IEnumerable<SpecMutation> mutations,
        IEnumerable<string>? renameTouchedSections = null)
    {
        var touchedSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            if (mutation.Kind == SpecMutationKind.RenameReference)
            {
                if (renameTouchedSections is not null)
                {
                    touchedSections.UnionWith(renameTouchedSections);
                }

                continue;
            }

            touchedSections.Add(mutation.Kind switch
            {
                SpecMutationKind.AddSource or SpecMutationKind.RemoveSource => "sources",
                SpecMutationKind.AddScopeClause => "scope",
                SpecMutationKind.AddCompute or SpecMutationKind.RemoveCompute => "compute",
                SpecMutationKind.SetMapLayer or SpecMutationKind.SetViewport => "map",
                SpecMutationKind.SetOutput => "outputs",
                _ => "compute"
            });
        }

        return AllSections().Where(touchedSections.Contains).ToArray();
    }

    private static string[] AllSections() => ["sources", "scope", "compute", "map", "outputs"];

    private static string[] DetermineChangedSections(string currentCanonicalJson, string nextCanonicalJson)
    {
        using var current = JsonDocument.Parse(currentCanonicalJson);
        using var next = JsonDocument.Parse(nextCanonicalJson);

        return AllSections()
            .Where(section => !SectionsMatch(current.RootElement, next.RootElement, section))
            .ToArray();
    }

    private static bool SectionsMatch(JsonElement currentRoot, JsonElement nextRoot, string section)
    {
        var hasCurrent = currentRoot.TryGetProperty(section, out var currentSection);
        var hasNext = nextRoot.TryGetProperty(section, out var nextSection);
        if (hasCurrent != hasNext)
        {
            return false;
        }

        return !hasCurrent ||
               string.Equals(currentSection.GetRawText(), nextSection.GetRawText(), StringComparison.Ordinal);
    }

    private static bool TryResolveTargetReference(SpecDocument document, string token, out string id)
    {
        var trimmed = token.Trim().TrimStart('@');
        foreach (var candidate in document.Sources.Select(source => source.Id)
            .Concat(document.Compute.Select(step => step.Id))
            .Concat(document.Outputs.Select(output => output.Id)))
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                id = candidate;
                return true;
            }
        }

        id = string.Empty;
        return false;
    }

    private static string? ResolveScopeTarget(SpecDocument document, SpecGroundingContext context, string tail)
    {
        if (!string.IsNullOrWhiteSpace(context.TargetId) && HasLocal(document, context.TargetId))
        {
            return context.TargetId;
        }

        foreach (var source in document.Sources)
        {
            if (tail.Contains(source.Id, StringComparison.OrdinalIgnoreCase))
            {
                return source.Id;
            }
        }

        return document.Sources.Length == 1 ? document.Sources[0].Id : null;
    }

    private static LayerDefinition? TryResolveLayerForSource(
        SpecDocument document,
        IReadOnlyList<LayerDefinition> layers,
        string sourceId)
    {
        var source = document.Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            return null;
        }

        var sourceRef = TryGetStringField(source.Properties, "ref");
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return null;
        }

        if (sourceRef.StartsWith("catalog:layer:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(sourceRef["catalog:layer:".Length..], out var layerId))
        {
            return layers.FirstOrDefault(layer => layer.Id == layerId);
        }

        return layers.FirstOrDefault(layer => string.Equals(layer.Name, sourceRef, StringComparison.OrdinalIgnoreCase));
    }

    private static Honua.Core.Features.Spec.Domain.SourceBinding? TryMatchExistingSource(SpecDocument document, string sourcePhrase)
    {
        var normalizedPhrase = NormalizeIdentifier(sourcePhrase);
        foreach (var source in document.Sources)
        {
            if (string.Equals(source.Id, normalizedPhrase, StringComparison.OrdinalIgnoreCase) ||
                sourcePhrase.Contains(source.Id, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            var title = TryGetStringField(source.Properties, "title");
            if (!string.IsNullOrWhiteSpace(title) &&
                title.Contains(sourcePhrase, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    private static string TryGetStringField(ObjectExpression properties, string key)
    {
        foreach (var field in properties.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal) &&
                field.Value is LiteralNode { Kind: SpecTypeKind.String } literal &&
                !string.IsNullOrWhiteSpace(literal.String))
            {
                return literal.String!;
            }
        }

        return string.Empty;
    }

    private static string ResolveSourceId(SpecDocument document, string baseId)
    {
        var candidate = NormalizeIdentifier(baseId);
        if (!HasLocal(document, candidate))
        {
            return candidate;
        }

        var suffix = 2;
        while (HasLocal(document, $"{candidate}_{suffix}"))
        {
            suffix++;
        }

        return $"{candidate}_{suffix}";
    }

    private static IEnumerable<LayerMatch> ResolveLayerCandidates(
        IReadOnlyList<LayerDefinition> layers,
        string phrase)
    {
        var tokens = GroundingTokenizer.TokenizeToSet(phrase);
        var candidates = layers
            .Select(layer => new LayerMatch(layer, ScoreLayer(layer, phrase, tokens)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Layer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        if (candidates.Length == 1 || candidates[0].Score - candidates[1].Score >= 2)
        {
            return [candidates[0]];
        }

        return candidates
            .TakeWhile(match => Math.Abs(match.Score - candidates[0].Score) < 0.01d)
            .Take(4);
    }

    private static double ScoreLayer(LayerDefinition layer, string phrase, ICollection<string> tokens)
    {
        var score = 0d;
        if (layer.Name.Contains(phrase, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        var layerTokens = GroundingTokenizer.TokenizeToSet($"{layer.Name} {layer.Description}");
        score += tokens.Count(token => layerTokens.Contains(token));

        if (string.Equals(layer.Name, phrase, StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private static FieldDefinition[] ResolveFieldCandidates(FieldDefinition[] fields, string text)
    {
        var textTokens = GroundingTokenizer.TokenizeToSet(text);
        var matches = fields
            .Select(field => new
            {
                Field = field,
                Score = textTokens.Count(token =>
                    field.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    field.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    Singularize(token).Equals(Singularize(field.Name), StringComparison.OrdinalIgnoreCase))
            })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Field.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length == 0)
        {
            return [];
        }

        var top = matches[0].Score;
        return matches
            .Where(entry => entry.Score == top)
            .Select(entry => entry.Field)
            .ToArray();
    }

    private static bool TryResolveLayerBySelection(
        IReadOnlyList<LayerDefinition> layers,
        string selection,
        out LayerDefinition layer)
    {
        if (selection.StartsWith("catalog:layer:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(selection["catalog:layer:".Length..], out var layerId))
        {
            layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)!;
            return layer is not null;
        }

        layer = layers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, selection, StringComparison.OrdinalIgnoreCase))!;
        return layer is not null;
    }

    private static bool TryGetSelectedLayer(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId,
        IReadOnlyList<LayerMatch> candidates,
        out LayerDefinition layer)
    {
        layer = null!;
        return TryGetSingleAnswer(clarificationAnswer, expectedIntentId, "dataset.selection", out var selection) &&
               TryResolveLayerBySelection(candidates.Select(static candidate => candidate.Layer).ToArray(), selection, out layer);
    }

    private static bool TryGetSelectedField(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId,
        IReadOnlyList<FieldDefinition> candidates,
        out FieldDefinition field)
    {
        field = null!;
        if (!TryGetSingleAnswer(clarificationAnswer, expectedIntentId, "column.selection", out var selection))
        {
            return false;
        }

        field = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, selection, StringComparison.OrdinalIgnoreCase))!;
        return field is not null;
    }

    private static bool TryGetSelectedValue(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId,
        IReadOnlyList<string> candidates,
        out string value)
    {
        value = string.Empty;
        if (!TryGetSingleAnswer(clarificationAnswer, expectedIntentId, "value.selection", out var selection))
        {
            return false;
        }

        var match = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate, selection, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        value = match;
        return true;
    }

    private static bool TryGetSingleAnswer(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId,
        string questionId,
        out string value)
    {
        value = string.Empty;
        var response = clarificationAnswer;
        if (response is null || !HasMatchingClarificationIntent(response, expectedIntentId))
        {
            return false;
        }

        if (!response.Answers.TryGetValue(questionId, out var values) || values.Count == 0)
        {
            return false;
        }

        value = values[0];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetSingleAnswer(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId,
        string questionId)
    {
        return TryGetSingleAnswer(clarificationAnswer, expectedIntentId, questionId, out var value)
            ? value
            : string.Empty;
    }

    private static bool IsConfirmed(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId,
        string questionId)
    {
        var value = GetSingleAnswer(clarificationAnswer, expectedIntentId, questionId);
        return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "confirm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMatchingClarificationIntent(
        ClarificationResponse? clarificationAnswer,
        string expectedIntentId)
        => clarificationAnswer is not null &&
           string.Equals(clarificationAnswer.IntentId, expectedIntentId, StringComparison.Ordinal);

    private static SpecClarificationEnvelope CreateDatasetClarification(IEnumerable<LayerMatch> candidates)
    {
        var list = candidates.ToArray();
        var questionId = "dataset.selection";
        return CreateClarificationEnvelope(
            ClarificationReasonCode.AmbiguousDataset,
            questionId,
            ClarificationQuestionKind.SingleSelect,
            "Which dataset should be used?",
            list.Select(match => new ClarificationOption
            {
                Id = $"catalog:layer:{match.Layer.Id}",
                Label = match.Layer.Name
            }).ToArray(),
            list.Select(match => new SpecClarificationCandidate
            {
                CandidateType = "dataset",
                Id = $"catalog:layer:{match.Layer.Id}",
                Label = match.Layer.Name,
                CatalogRef = $"catalog:layer:{match.Layer.Id}",
                SchemaPreview = match.Layer.Fields
                    .Where(field => !field.IsGeometry)
                    .Take(5)
                    .Select(field => field.Name)
                    .ToArray()
            }).ToArray());
    }

    private static SpecClarificationEnvelope CreateColumnClarification(IEnumerable<FieldDefinition> fields)
    {
        var list = fields.ToArray();
        var questionId = "column.selection";
        return CreateClarificationEnvelope(
            ClarificationReasonCode.AmbiguousColumn,
            questionId,
            ClarificationQuestionKind.SingleSelect,
            "Which column should the filter use?",
            list.Select(field => new ClarificationOption
            {
                Id = field.Name,
                Label = field.Name
            }).ToArray(),
            list.Select(field => new SpecClarificationCandidate
            {
                CandidateType = "column",
                Id = field.Name,
                Label = field.Name,
                ColumnName = field.Name,
                TypeRef = field.Type.ToString(),
                Nullable = field.Nullable,
                Sample = field.Description
            }).ToArray());
    }

    private static SpecClarificationEnvelope CreateValueClarification(IEnumerable<string> values)
    {
        var list = values.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var questionId = "value.selection";
        return CreateClarificationEnvelope(
            ClarificationReasonCode.AmbiguousFilterValue,
            questionId,
            ClarificationQuestionKind.SingleSelect,
            "Which value should the filter use?",
            list.Select(value => new ClarificationOption
            {
                Id = value,
                Label = value
            }).ToArray(),
            list.Select(value => new SpecClarificationCandidate
            {
                CandidateType = "value",
                Id = value,
                Label = value,
                Value = value
            }).ToArray());
    }

    private static SpecClarificationEnvelope CreateUnitClarification()
    {
        const string questionId = "unit.selection";
        return CreateClarificationEnvelope(
            ClarificationReasonCode.AmbiguousUnit,
            questionId,
            ClarificationQuestionKind.SingleSelect,
            "Which unit should be used?",
            UnitCandidates.Select(unit => new ClarificationOption { Id = unit, Label = unit }).ToArray(),
            UnitCandidates.Select(unit => new SpecClarificationCandidate
            {
                CandidateType = "unit",
                Id = unit,
                Label = unit,
                Unit = unit
            }).ToArray());
    }

    private static SpecClarificationEnvelope CreateCrsClarification()
    {
        const string questionId = "crs.selection";
        return CreateClarificationEnvelope(
            ClarificationReasonCode.AmbiguousCrs,
            questionId,
            ClarificationQuestionKind.SingleSelect,
            "Which projected CRS should be used?",
            CrsCandidates.Select(crs => new ClarificationOption { Id = crs, Label = crs }).ToArray(),
            CrsCandidates.Select(crs => new SpecClarificationCandidate
            {
                CandidateType = "crs",
                Id = crs,
                Label = crs,
                Crs = crs
            }).ToArray());
    }

    private static SpecClarificationEnvelope CreateOperatorClarification()
    {
        const string questionId = "operator.selection";
        var operators = new[]
        {
            new SpecClarificationCandidate { CandidateType = "operator", Id = "buffer", Label = "buffer", OperatorName = "buffer" },
            new SpecClarificationCandidate { CandidateType = "operator", Id = "spatial_join", Label = "spatial_join", OperatorName = "spatial_join" }
        };

        return CreateClarificationEnvelope(
            ClarificationReasonCode.AmbiguousProcess,
            questionId,
            ClarificationQuestionKind.SingleSelect,
            "Which operation should be used?",
            operators.Select(candidate => new ClarificationOption
            {
                Id = candidate.Id,
                Label = candidate.Label
            }).ToArray(),
            operators);
    }

    private static SpecClarificationEnvelope CreateHeavyConfirmation(string operatorName)
    {
        const string questionId = "heavy.confirm";
        return CreateClarificationEnvelope(
            ClarificationReasonCode.HeavyOperationConfirmation,
            questionId,
            ClarificationQuestionKind.Confirmation,
            $"Confirm that the heavy operator '{operatorName}' should be added.",
            options: null,
            candidates: []);
    }

    private static SpecClarificationEnvelope CreateClarificationEnvelope(
        ClarificationReasonCode reasonCode,
        string questionId,
        ClarificationQuestionKind questionKind,
        string prompt,
        IReadOnlyList<ClarificationOption>? options,
        IReadOnlyList<SpecClarificationCandidate> candidates)
    {
        var question = new ClarificationQuestion
        {
            QuestionId = questionId,
            Kind = questionKind,
            Prompt = prompt,
            Options = options
        };

        var request = new ClarificationRequest
        {
            IntentId = BuildIntentId(reasonCode, question, candidates),
            ReasonCodes = [reasonCode],
            Questions = [question]
        };

        return new SpecClarificationEnvelope(
            request,
            new Dictionary<string, IReadOnlyList<SpecClarificationCandidate>>(StringComparer.Ordinal)
            {
                [questionId] = candidates
            });
    }

    private static string BuildIntentId(
        ClarificationReasonCode reasonCode,
        ClarificationQuestion question,
        IReadOnlyList<SpecClarificationCandidate> candidates)
    {
        // The intent id fingerprints the outstanding clarification state so
        // stale answers cannot be rebound to a different candidate set.
        var builder = new StringBuilder()
            .Append("reason=").Append(reasonCode).Append('\n')
            .Append("question=").Append(question.QuestionId).Append('\n')
            .Append("kind=").Append(question.Kind).Append('\n')
            .Append("prompt=").Append(question.Prompt).Append('\n');

        foreach (var option in question.Options ?? [])
        {
            builder
                .Append("option=").Append(option.Id).Append('|')
                .Append(option.Label).Append('\n');
        }

        foreach (var candidate in candidates)
        {
            builder
                .Append("candidate=").Append(candidate.CandidateType).Append('|')
                .Append(candidate.Id).Append('|')
                .Append(candidate.Label).Append('|')
                .Append(candidate.CatalogRef ?? string.Empty).Append('|')
                .Append(string.Join(",", candidate.SchemaPreview ?? Array.Empty<string>())).Append('|')
                .Append(candidate.ColumnName ?? string.Empty).Append('|')
                .Append(candidate.TypeRef ?? string.Empty).Append('|')
                .Append(candidate.Nullable?.ToString() ?? string.Empty).Append('|')
                .Append(candidate.Sample ?? string.Empty).Append('|')
                .Append(candidate.Value ?? string.Empty).Append('|')
                .Append(candidate.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
                .Append(candidate.Unit ?? string.Empty).Append('|')
                .Append(candidate.Crs ?? string.Empty).Append('|')
                .Append(candidate.OperatorName ?? string.Empty).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"spec-grounding-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static bool RequiresHeavyConfirmation(string value)
        => value.Contains("zonal_stats", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("zonal stats", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("zonal statistics", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNearAmbiguity(string value)
        => value.Contains(" near ", StringComparison.OrdinalIgnoreCase);

    private static bool HasDirectOperator(string value)
        => value.Contains("buffer", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("spatial_join", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("spatial join", StringComparison.OrdinalIgnoreCase);

    private static string TrimKnownFieldSuffix(string text, string fieldName)
    {
        var trimmed = text.Trim();
        if (trimmed.EndsWith(fieldName, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^fieldName.Length].Trim();
        }

        var singularField = Singularize(fieldName);
        if (trimmed.EndsWith(singularField, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^singularField.Length].Trim();
        }

        return trimmed.Trim('\'', '"');
    }

    private static string NormalizeIdentifier(string value)
    {
        var tokens = GroundingTokenizer.Tokenize(value);
        return tokens.Count == 0 ? "item" : string.Join('_', tokens);
    }

    private static string Singularize(string value)
        => value.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            ? value[..^1]
            : value;

    private static bool TryParseSummarySource(string clause, out (string SourceId, string SourcePhrase) result)
    {
        var match = SummarySourcePattern().Match(clause);
        if (match.Success)
        {
            result = (match.Groups["id"].Value, match.Groups["dataset"].Value);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseUseDataset(string clause, out (string? SourceId, string SourcePhrase) result)
    {
        var match = UseDatasetPattern().Match(clause);
        if (match.Success)
        {
            result = (
                match.Groups["id"].Success ? match.Groups["id"].Value : null,
                match.Groups["dataset"].Value);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseSummaryScope(string clause, out (string TargetId, string Predicate) result)
    {
        var match = SummaryScopePattern().Match(clause);
        if (match.Success)
        {
            result = (match.Groups["target"].Value.Trim().TrimStart('@'), match.Groups["predicate"].Value.Trim());
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseRename(string clause, out (string FromId, string ToId) result)
    {
        var match = RenamePattern().Match(clause);
        if (match.Success)
        {
            result = (match.Groups["from"].Value, match.Groups["to"].Value);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseRemoveSource(string clause, out string result)
    {
        var match = RemoveSourcePattern().Match(clause);
        if (match.Success)
        {
            result = match.Groups["id"].Value;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryParseRemoveCompute(string clause, out string result)
    {
        var match = RemoveComputePattern().Match(clause);
        if (match.Success)
        {
            result = match.Groups["id"].Value;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryParseOnlyClause(string clause, out string tail)
    {
        var match = OnlyPattern().Match(clause);
        if (match.Success)
        {
            tail = match.Groups["tail"].Value.Trim();
            return true;
        }

        tail = string.Empty;
        return false;
    }

    private static bool TryParseSummaryCompute(
        string clause,
        out (string ComputeId, string OperatorName, Dictionary<string, string> Inputs, Dictionary<string, string>? Parameters) result)
    {
        var match = SummaryComputePattern().Match(clause);
        if (match.Success)
        {
            var inputs = match.Groups["inputs"].Success
                ? ParseKeyValueList(match.Groups["inputs"].Value)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var parameters = match.Groups["params"].Success
                ? ParseKeyValueList(match.Groups["params"].Value)
                : null;
            result = (match.Groups["id"].Value, match.Groups["op"].Value, inputs, parameters);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseBuffer(
        string clause,
        out (string Target, string Distance, string? Unit, string? Crs, string? ComputeId) result)
    {
        var match = BufferPattern().Match(clause);
        if (match.Success)
        {
            result = (
                match.Groups["target"].Value,
                match.Groups["distance"].Value,
                match.Groups["unit"].Success ? match.Groups["unit"].Value : null,
                match.Groups["crs"].Success ? match.Groups["crs"].Value.ToUpperInvariant() : null,
                match.Groups["id"].Success ? match.Groups["id"].Value : null);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseWithin(
        string clause,
        out (string LeftPhrase, string Distance, string? Unit, string RightPhrase, string? Crs) result)
    {
        var match = WithinPattern().Match(clause);
        if (match.Success)
        {
            result = (
                match.Groups["left"].Value.Trim(),
                match.Groups["distance"].Value,
                match.Groups["unit"].Success ? match.Groups["unit"].Value : null,
                match.Groups["right"].Value.Trim(),
                match.Groups["crs"].Success ? match.Groups["crs"].Value.ToUpperInvariant() : null);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseShowMap(string clause, out List<string> result)
    {
        var match = ShowMapPattern().Match(clause);
        if (match.Success)
        {
            var raw = match.Groups["layers"].Value;
            var ids = raw.Split([",", " and "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            result = ids;
            return true;
        }

        result = [];
        return false;
    }

    private static bool TryParseViewport(
        string clause,
        out (double Longitude, double Latitude, int Zoom) result)
    {
        var match = ViewportPattern().Match(clause);
        if (match.Success)
        {
            result = (
                double.Parse(match.Groups["lon"].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(match.Groups["lat"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["zoom"].Value, System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseOutput(string clause, out (string OutputId, string Expression) result)
    {
        var match = OutputPattern().Match(clause);
        if (match.Success)
        {
            result = (match.Groups["id"].Value, match.Groups["expr"].Value.Trim());
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseReproject(
        string clause,
        out (string Target, string? Crs, string? ComputeId) result)
    {
        var match = ReprojectPattern().Match(clause);
        if (match.Success)
        {
            result = (
                match.Groups["target"].Value,
                match.Groups["crs"].Success ? match.Groups["crs"].Value.ToUpperInvariant() : null,
                match.Groups["id"].Success ? match.Groups["id"].Value : null);
            return true;
        }

        result = default;
        return false;
    }

    private static Dictionary<string, string> ParseKeyValueList(string value)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in value.Split(" and ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = segment[..index].Trim();
            var itemValue = segment[(index + 1)..].Trim();
            if (itemValue.StartsWith("center ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pairs[key] = itemValue;
        }

        return pairs;
    }

    [GeneratedRegex(@"(?<=[A-Za-z0-9""])\.\s+|[\r\n;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreakPattern();

    [GeneratedRegex(@"^source\s+(?<id>[a-z0-9_]+)\s+uses\s+dataset\s+(?<dataset>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummarySourcePattern();

    [GeneratedRegex(@"^(?:use|add)\s+(?<dataset>[a-z0-9_@:\-\s]+?)(?:\s+dataset)?(?:\s+as\s+(?<id>[a-z0-9_]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UseDatasetPattern();

    [GeneratedRegex(@"^filters\s+(?<target>@?[a-z0-9_]+)\s+where\s+(?<predicate>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryScopePattern();

    [GeneratedRegex(@"^rename\s+(?<from>[a-z0-9_]+)\s+to\s+(?<to>[a-z0-9_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RenamePattern();

    [GeneratedRegex(@"^(?:remove|drop)\s+source\s+(?<id>[a-z0-9_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoveSourcePattern();

    [GeneratedRegex(@"^(?:remove|drop)\s+compute\s+(?<id>[a-z0-9_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoveComputePattern();

    [GeneratedRegex(@"^only\s+(?<tail>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlyPattern();

    [GeneratedRegex(@"^runs\s+(?<op>[a-z0-9_]+)\s+as\s+(?<id>[a-z0-9_]+)(?:\s+on\s+(?<inputs>.*?))?(?:\s+using\s+(?<params>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryComputePattern();

    [GeneratedRegex(@"^buffer\s+(?<target>@?[a-z0-9_]+)\s+by\s+(?<distance>\d+(?:\.\d+)?)(?:\.(?<unit>km|m|mi|ft|nm)|\s+(?<unit>km|m|mi|ft|nm))?(?:\s+(?:in|using)\s+(?<crs>epsg:\d+))?(?:\s+as\s+(?<id>[a-z0-9_]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BufferPattern();

    [GeneratedRegex(@"^(?<left>[a-z0-9_\s]+?)\s+within\s+(?<distance>\d+(?:\.\d+)?)(?:\.(?<unit>km|m|mi|ft|nm)|\s+(?<unit>km|m|mi|ft|nm))?\s+of\s+(?<right>[a-z0-9_\s]+?)(?:\s+(?:in|using)\s+(?<crs>epsg:\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WithinPattern();

    [GeneratedRegex(@"^(?:show|shows|display|displays)(?:\s+layers?)?\s+(?<layers>.+?)\s+on\s+the\s+map$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShowMapPattern();

    [GeneratedRegex(@"^viewport\s+center\s+(?<lon>-?\d+(?:\.\d+)?)\s+(?<lat>-?\d+(?:\.\d+)?)\s+zoom\s+(?<zoom>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ViewportPattern();

    [GeneratedRegex(@"^output\s+(?<id>[a-z0-9_]+)\s+returns\s+(?<expr>@?[a-z0-9_\.]+(?:\(\))?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutputPattern();

    [GeneratedRegex(@"^reproject\s+(?<target>@?[a-z0-9_]+)(?:\s+to\s+(?<crs>epsg:\d+))?(?:\s+as\s+(?<id>[a-z0-9_]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReprojectPattern();

    private sealed record LayerMatch(LayerDefinition Layer, double Score);

    private sealed record SourceResolution
    {
        public bool IsSuccess { get; init; }

        public string SourceId { get; init; } = string.Empty;

        public AddSourceMutation? AddSourceMutation { get; init; }

        public SpecClarificationEnvelope? Clarification { get; init; }

        public SpecGroundingErrorKind? ErrorKind { get; init; }

        public string? ErrorMessage { get; init; }

        public static SourceResolution Success(string sourceId, AddSourceMutation? addSourceMutation)
            => new() { IsSuccess = true, SourceId = sourceId, AddSourceMutation = addSourceMutation };

        public static SourceResolution FromClarification(SpecClarificationEnvelope clarification)
            => new() { Clarification = clarification };

        public static SourceResolution Error(SpecGroundingErrorKind errorKind, string errorMessage)
            => new() { ErrorKind = errorKind, ErrorMessage = errorMessage };

        public ClausePlanResult ToClauseResult()
        {
            if (Clarification is not null)
            {
                return ClausePlanResult.FromClarification(Clarification);
            }

            if (ErrorKind is not null)
            {
                return ClausePlanResult.Error(ErrorKind.Value, ErrorMessage ?? string.Empty);
            }

            if (AddSourceMutation is not null)
            {
                return ClausePlanResult.FromMutation(AddSourceMutation);
            }

            return ClausePlanResult.None();
        }
    }

    private sealed record ClausePlanResult
    {
        public IReadOnlyList<SpecMutation> Mutations { get; init; } = [];

        public SpecClarificationEnvelope? Clarification { get; init; }

        public SpecGroundingErrorKind? ErrorKind { get; init; }

        public string? ErrorMessage { get; init; }

        public IReadOnlyList<SpecDiagnostic> Warnings { get; init; } = [];

        public static ClausePlanResult None() => new();

        public static ClausePlanResult FromMutation(SpecMutation mutation)
            => new() { Mutations = [mutation] };

        public static ClausePlanResult FromMutations(IReadOnlyList<SpecMutation> mutations)
            => new() { Mutations = mutations };

        public static ClausePlanResult FromClarification(SpecClarificationEnvelope clarification)
            => new() { Clarification = clarification };

        public static ClausePlanResult Error(SpecGroundingErrorKind errorKind, string message)
            => new() { ErrorKind = errorKind, ErrorMessage = message };
    }
}
