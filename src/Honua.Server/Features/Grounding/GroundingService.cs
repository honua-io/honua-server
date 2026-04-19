// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Grounding;

/// <summary>
/// Coordinates the grounding pipeline: engine-driven classification and
/// ranking, authorization filtering, material-ambiguity evaluation,
/// intent drafting, and provenance wiring. Owns no session state; callers
/// carry intent + clarification state across turns.
/// </summary>
internal sealed class GroundingService : IGroundingService
{
    private readonly IGroundingEngine _engine;
    private readonly IProcessCatalog _processCatalog;
    private readonly ILayerCatalog? _layerCatalog;
    private readonly IGroundingAuthorizationFilter _authorizationFilter;
    private readonly GroundingOptions _options;
    private readonly ILogger<GroundingService> _logger;

    public GroundingService(
        IGroundingEngine engine,
        IProcessCatalog processCatalog,
        IGroundingAuthorizationFilter authorizationFilter,
        IOptions<GroundingOptions> options,
        ILogger<GroundingService> logger,
        ILayerCatalog? layerCatalog = null)
    {
        _engine = engine;
        _processCatalog = processCatalog;
        _authorizationFilter = authorizationFilter;
        _options = options.Value;
        _logger = logger;
        _layerCatalog = layerCatalog;
    }

    public async Task<GroundingResult> GroundAsync(
        GroundingRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);

        if (string.IsNullOrWhiteSpace(request.Goal))
        {
            GroundingLog.PassRejected(_logger, nameof(GroundingErrorKind.EmptyGoal), "empty goal");
            throw new GroundingException(GroundingErrorKind.EmptyGoal, "Grounding request goal must be non-empty.");
        }

        var tokens = GroundingTokenizer.Tokenize(request.Goal);
        GroundingLog.PassStarted(_logger, _engine.Name, tokens.Count, request.WorkflowFamilyHint.HasValue);

        // 1. Classify the workflow family.
        var classification = _engine.Classify(request);

        // 2. Rank processes against the frozen process catalog. The catalog
        // is a singleton and contains the deterministic 20-process roster;
        // no IO is required here.
        var processes = _processCatalog.ListProcesses();
        var processCandidates = _engine.ScoreProcesses(request, processes);
        processCandidates = ApplyBandsAndCap(processCandidates);
        processCandidates = _authorizationFilter.Filter(principal, processCandidates);

        // 3. Rank layers / services. Layer catalog is optional because some
        // deployments (e.g. read-only raster servers, pre-provisioning test
        // harnesses) may not have it wired. Missing catalog → empty dataset
        // list rather than a hard failure; the clarification envelope will
        // prompt for explicit inputs.
        var datasetCandidates = await RankDatasetsAsync(request, cancellationToken).ConfigureAwait(false);
        datasetCandidates = ApplyBandsAndCap(datasetCandidates);
        datasetCandidates = _authorizationFilter.Filter(principal, datasetCandidates);

        var ranking = new CandidateRanking
        {
            Datasets = datasetCandidates,
            Processes = processCandidates
        };

        // 4. Evaluate material ambiguity against the post-filter ranking so
        // the clarification envelope only names candidates the caller can
        // actually see.
        var requiredParameterGaps = CollectRequiredParameterGaps(request, ranking);
        var findings = MaterialAmbiguityEvaluator.Evaluate(
            request,
            classification,
            ranking,
            requiredParameterGaps,
            _options);

        // 5. Build draft intent.
        var intentId = request.IntentId ?? $"grounding-{Guid.NewGuid():N}";
        var assumptions = CollectAssumptions(request);
        var clarificationQuestionIds = findings.Select(f => f.QuestionId).ToArray();
        var draft = IntentDrafter.Draft(
            request,
            intentId,
            classification,
            ranking,
            clarificationQuestionIds,
            assumptions);

        // 6. Build the clarification envelope (if any).
        var clarification = BuildClarification(intentId, findings);

        GroundingLog.PassCompleted(
            _logger,
            classification.Value.ToString(),
            classification.Confidence,
            processCandidates.Count + datasetCandidates.Count,
            findings.Count);

        return new GroundingResult
        {
            WorkflowFamily = classification,
            DraftIntent = draft,
            Candidates = ranking,
            Clarification = clarification,
            Engine = _engine.Name
        };
    }

    private async Task<IReadOnlyList<GroundingCandidate>> RankDatasetsAsync(
        GroundingRequest request,
        CancellationToken cancellationToken)
    {
        if (_layerCatalog is null)
        {
            return [];
        }

        LayerDefinition[] layerDefs;
        ServiceDefinition[] serviceDefs;
        try
        {
            layerDefs = await _layerCatalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);
            serviceDefs = await _layerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GroundingLog.PassRejected(_logger, nameof(GroundingErrorKind.CatalogUnavailable), ex.Message);
            return [];
        }

        var layerCandidates = layerDefs
            .Select(l => new LayerCandidate(l.Id, l.Name, l.Description))
            .ToArray();
        var serviceCandidates = serviceDefs
            .Select(s => new ServiceCandidate(s.Name, s.Description))
            .ToArray();

        var layerScores = _engine.ScoreLayers(request, layerCandidates);
        var serviceScores = _engine.ScoreServices(request, serviceCandidates);

        if (layerScores.Count == 0)
        {
            return serviceScores;
        }

        if (serviceScores.Count == 0)
        {
            return layerScores;
        }

        var merged = new List<GroundingCandidate>(layerScores.Count + serviceScores.Count);
        merged.AddRange(layerScores);
        merged.AddRange(serviceScores);
        merged.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return merged;
    }

    private IReadOnlyList<GroundingCandidate> ApplyBandsAndCap(IReadOnlyList<GroundingCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var limit = Math.Min(candidates.Count, _options.MaxCandidatesPerKind);
        var capped = new List<GroundingCandidate>(limit);
        for (var i = 0; i < limit; i++)
        {
            capped.Add(candidates[i] with
            {
                ConfidenceBand = BandFor(candidates[i].Score)
            });
        }

        return capped;
    }

    private ConfidenceBand BandFor(double score)
    {
        if (score >= _options.HighConfidenceFloor)
        {
            return ConfidenceBand.High;
        }

        if (score >= _options.MediumConfidenceFloor)
        {
            return ConfidenceBand.Medium;
        }

        return ConfidenceBand.Low;
    }

    private List<ProcessParameterSpec> CollectRequiredParameterGaps(
        GroundingRequest request,
        CandidateRanking ranking)
    {
        // Only probe the top process candidate: if the operator has not
        // settled on a process, the AmbiguousProcess finding handles it,
        // and walking every candidate would produce noise.
        if (ranking.Processes.Count == 0)
        {
            return [];
        }

        var topProcessId = ranking.Processes[0].Id;
        var process = _processCatalog.GetProcess(topProcessId);
        if (process is null)
        {
            return [];
        }

        var explicitTokens = GroundingTokenizer.TokenizeToSet(request.Goal);
        var gaps = new List<ProcessParameterSpec>(capacity: 4);
        foreach (var parameter in process.Parameters)
        {
            if (!parameter.Required)
            {
                continue;
            }

            if (parameter.DefaultValue is not null)
            {
                continue;
            }

            if (explicitTokens.Contains(parameter.Name.ToLowerInvariant()))
            {
                continue;
            }

            if (IsSatisfiedByConstraints(parameter, request.Constraints))
            {
                continue;
            }

            gaps.Add(parameter);
        }

        return gaps;
    }

    private static bool IsSatisfiedByConstraints(
        ProcessParameterSpec parameter,
        IntentConstraints? constraints)
    {
        if (constraints is null)
        {
            return false;
        }

        return parameter.ValueType switch
        {
            ProcessParameterValueType.Srid => constraints.SpatialReferenceId.HasValue,
            _ => false
        };
    }

    private static List<string> CollectAssumptions(GroundingRequest request)
    {
        var assumptions = new List<string>(capacity: 2);
        if (request.Constraints?.Units is { Length: > 0 } units)
        {
            assumptions.Add($"units={units}");
        }

        if (request.Constraints?.SpatialReferenceId is { } srid)
        {
            assumptions.Add($"srid={srid}");
        }
        else if (request.Constraints is not null)
        {
            // AOI supplied without an SRID defaults to EPSG:4326 per
            // IntentConstraints semantics — record it as an assumption so
            // downstream planning can flag divergence.
            assumptions.Add("srid=4326 (default)");
        }

        return assumptions;
    }

    private static ClarificationRequest? BuildClarification(
        string intentId,
        IReadOnlyList<MaterialAmbiguityFinding> findings)
    {
        if (findings.Count == 0)
        {
            return null;
        }

        var reasons = new List<ClarificationReasonCode>(findings.Count);
        var questions = new List<ClarificationQuestion>(findings.Count);
        foreach (var finding in findings)
        {
            reasons.Add(finding.ReasonCode);
            questions.Add(new ClarificationQuestion
            {
                QuestionId = finding.QuestionId,
                Kind = finding.QuestionKind,
                Prompt = finding.Prompt,
                Options = finding.Options
            });
        }

        return new ClarificationRequest
        {
            IntentId = intentId,
            ReasonCodes = reasons,
            Questions = questions
        };
    }
}
