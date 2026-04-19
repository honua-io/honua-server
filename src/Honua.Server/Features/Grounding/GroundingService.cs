// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly IGroundingAuthorizationFilter _authorizationFilter;
    private readonly GroundingOptions _options;
    private readonly ILogger<GroundingService> _logger;

    // Preferred constructor used by DI: the service stays a singleton and
    // resolves the scoped ILayerCatalog per call via IServiceScopeFactory.
    public GroundingService(
        IGroundingEngine engine,
        IProcessCatalog processCatalog,
        IGroundingAuthorizationFilter authorizationFilter,
        IOptions<GroundingOptions> options,
        ILogger<GroundingService> logger,
        IServiceScopeFactory serviceScopeFactory)
        : this(engine, processCatalog, authorizationFilter, options, logger, serviceScopeFactory, layerCatalog: null)
    {
    }

    // Direct-catalog constructor retained for unit tests that supply a
    // substituted ILayerCatalog without wiring a scope factory.
    internal GroundingService(
        IGroundingEngine engine,
        IProcessCatalog processCatalog,
        IGroundingAuthorizationFilter authorizationFilter,
        IOptions<GroundingOptions> options,
        ILogger<GroundingService> logger,
        IServiceScopeFactory? serviceScopeFactory,
        ILayerCatalog? layerCatalog)
    {
        _engine = engine;
        _processCatalog = processCatalog;
        _authorizationFilter = authorizationFilter;
        _options = options.Value;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
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

        // Resolve prior-turn clarification answers. Validated overrides flow
        // into classification, ranking, and drafting; tracked question ids
        // also suppress the matching finding so the next turn does not
        // replay the same ambiguity. Invalid enum answers (workflow_family,
        // publish.target) throw here so the caller receives invalid_argument
        // instead of a silently ignored choice.
        var applied = ClarificationAnswerResolver.Parse(request.ClarificationResponse);
        var appliedIds = new HashSet<string>(applied.AppliedQuestionIds, StringComparer.Ordinal);

        // 1. Classify the workflow family. A prior `workflow_family` answer
        // overrides the engine output at 1.0 confidence with a pinned
        // evidence tag so downstream consumers can audit the source.
        var classification = applied.WorkflowFamilyOverride is { } overrideFamily
            ? new WorkflowFamilyClassification
            {
                Value = overrideFamily,
                Confidence = 1.0,
                Evidence = ["clarification"]
            }
            : _engine.Classify(request);

        // 2. Rank processes against the frozen process catalog. The catalog
        // is a singleton and contains the deterministic 20-process roster;
        // no IO is required here.
        var processes = _processCatalog.ListProcesses();
        var processCandidates = _engine.ScoreProcesses(request, processes);
        processCandidates = ApplyBandsAndCap(processCandidates);
        processCandidates = _authorizationFilter.Filter(principal, processCandidates);
        processCandidates = ClarificationAnswerResolver.ApplyPin(
            processCandidates, applied.PinnedProcessId, "process.selection", appliedIds);

        // 3. Rank layers / services. Layer catalog is optional because some
        // deployments (e.g. read-only raster servers, pre-provisioning test
        // harnesses) may not have it wired. Missing catalog → empty dataset
        // list rather than a hard failure; the clarification envelope will
        // prompt for explicit inputs.
        var datasetCandidates = await RankDatasetsAsync(request, cancellationToken).ConfigureAwait(false);
        datasetCandidates = ApplyBandsAndCap(datasetCandidates);
        datasetCandidates = _authorizationFilter.Filter(principal, datasetCandidates);
        datasetCandidates = ClarificationAnswerResolver.ApplyPin(
            datasetCandidates, applied.PinnedDatasetId, "dataset.selection", appliedIds);

        var ranking = new CandidateRanking
        {
            Datasets = datasetCandidates,
            Processes = processCandidates
        };

        // 4. Evaluate material ambiguity against the post-filter ranking so
        // the clarification envelope only names candidates the caller can
        // actually see. Parameter-gap probing consumes resolved param.<name>
        // values; the finding filter below consumes the remaining answered
        // kinds (workflow_family, dataset.selection, process.selection,
        // publish.target, destructive.confirm, workflow_family.blocked).
        var requiredParameterGaps = CollectRequiredParameterGaps(request, ranking, applied.ResolvedParameters);
        var findings = MaterialAmbiguityEvaluator.Evaluate(
            request,
            classification,
            ranking,
            requiredParameterGaps,
            _options);
        findings = FilterAnsweredFindings(findings, appliedIds);

        // 5. Build draft intent.
        var intentId = request.IntentId ?? $"grounding-{Guid.NewGuid():N}";
        var assumptions = CollectAssumptions(request, applied.ResolvedParameters);
        var clarificationQuestionIds = findings.Select(f => f.QuestionId).ToArray();
        var draft = IntentDrafter.Draft(
            request,
            intentId,
            classification,
            ranking,
            clarificationQuestionIds,
            appliedIds,
            assumptions,
            applied.PublishTargetOverride);

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
        // Direct-catalog path for unit tests that inject a substituted
        // ILayerCatalog without a scope factory.
        if (_layerCatalog is not null)
        {
            return await ScoreDatasetsFromCatalogAsync(_layerCatalog, request, cancellationToken).ConfigureAwait(false);
        }

        // Production path: resolve the scoped ILayerCatalog inside a fresh
        // DI scope so database connections cycle per request instead of
        // being captured by the singleton GroundingService.
        if (_serviceScopeFactory is null)
        {
            return [];
        }

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetService<ILayerCatalog>();
        if (catalog is null)
        {
            return [];
        }

        return await ScoreDatasetsFromCatalogAsync(catalog, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GroundingCandidate>> ScoreDatasetsFromCatalogAsync(
        ILayerCatalog catalog,
        GroundingRequest request,
        CancellationToken cancellationToken)
    {
        LayerDefinition[] layerDefs;
        ServiceDefinition[] serviceDefs;
        try
        {
            layerDefs = await catalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);
            serviceDefs = await catalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
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
        CandidateRanking ranking,
        IReadOnlyDictionary<string, string> resolvedParameters)
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

            if (resolvedParameters.ContainsKey(parameter.Name))
            {
                continue;
            }

            gaps.Add(parameter);
        }

        return gaps;
    }

    private static IReadOnlyList<MaterialAmbiguityFinding> FilterAnsweredFindings(
        IReadOnlyList<MaterialAmbiguityFinding> findings,
        HashSet<string> answeredQuestionIds)
    {
        if (answeredQuestionIds.Count == 0 || findings.Count == 0)
        {
            return findings;
        }

        var retained = new List<MaterialAmbiguityFinding>(findings.Count);
        foreach (var finding in findings)
        {
            if (!answeredQuestionIds.Contains(finding.QuestionId))
            {
                retained.Add(finding);
            }
        }

        return retained;
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

    private static List<string> CollectAssumptions(
        GroundingRequest request,
        IReadOnlyDictionary<string, string> resolvedParameters)
    {
        var assumptions = new List<string>(capacity: 2 + resolvedParameters.Count);
        if (request.Constraints?.Units is { Length: > 0 } units)
        {
            assumptions.Add($"units={units}");
        }

        if (request.Constraints?.SpatialReferenceId is { } srid)
        {
            assumptions.Add($"srid={srid}");
        }
        else if (!string.IsNullOrWhiteSpace(request.Constraints?.AreaOfInterest))
        {
            // AOI supplied without an SRID defaults to EPSG:4326 per
            // IntentConstraints semantics — record it as an assumption so
            // downstream planning can flag divergence. Constraints without a
            // spatial input (units-only, time-only) stay silent so the
            // assumption list reflects the caller's actual spatial footprint.
            assumptions.Add("srid=4326 (default)");
        }

        // Resolved parameter values from prior clarification answers flow
        // through as assumptions so downstream planning sees the caller's
        // choices without a new contract surface.
        foreach (var (name, value) in resolvedParameters)
        {
            assumptions.Add($"param.{name}={value}");
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
