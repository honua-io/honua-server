// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Geoprocessing;
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
    private readonly IMetadataV2GraphProvider? _metadataGraphProvider;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly IGroundingAuthorizationFilter _authorizationFilter;
    private readonly GroundingOptions _options;
    private readonly ILogger<GroundingService> _logger;

    // Preferred constructor used by DI: the service stays a singleton and resolves
    // the scoped Metadata V2 graph provider per call via IServiceScopeFactory.
    public GroundingService(
        IGroundingEngine engine,
        IProcessCatalog processCatalog,
        IGroundingAuthorizationFilter authorizationFilter,
        IOptions<GroundingOptions> options,
        ILogger<GroundingService> logger,
        IServiceScopeFactory serviceScopeFactory)
        : this(engine, processCatalog, authorizationFilter, options, logger, serviceScopeFactory, metadataGraphProvider: null)
    {
    }

    // Direct-provider constructor retained for unit tests that supply a
    // substituted Metadata V2 graph provider without wiring a scope factory.
    internal GroundingService(
        IGroundingEngine engine,
        IProcessCatalog processCatalog,
        IGroundingAuthorizationFilter authorizationFilter,
        IOptions<GroundingOptions> options,
        ILogger<GroundingService> logger,
        IServiceScopeFactory? serviceScopeFactory,
        IMetadataV2GraphProvider? metadataGraphProvider)
    {
        _engine = engine;
        _processCatalog = processCatalog;
        _authorizationFilter = authorizationFilter;
        _options = options.Value;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _metadataGraphProvider = metadataGraphProvider;
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

        // Clarification turns must carry matching request + response intent ids
        // so answers cannot be applied to a different intent. The MCP tool
        // mapper copies the same value into both fields, but IGroundingService
        // is the public abstraction for future adapters — enforce the contract
        // here so callers receive invalid_argument instead of silently rebinding
        // one intent's answers onto another.
        EnsureClarificationIntentIdMatches(request);

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
        // no IO is required here. Authorization runs before the shortlist
        // cap so denied top hits cannot consume the MaxCandidatesPerKind
        // budget and hide an authorized candidate ranked just below them.
        // Pin application also runs before the cap so a still-authorized
        // selection that has fallen just below MaxCandidatesPerKind is
        // promoted to the front instead of being trimmed and then rejected
        // as invalid_argument. The contract pins against the post-
        // authorization ranking (see docs/developer/GROUNDING.md).
        var processes = _processCatalog.ListProcesses();
        var processCandidates = _engine.ScoreProcesses(request, processes);
        processCandidates = _authorizationFilter.Filter(principal, processCandidates);
        processCandidates = ClarificationAnswerResolver.ApplyPin(
            processCandidates, applied.PinnedProcessId, "process.selection", appliedIds);
        processCandidates = ApplyBandsAndCap(processCandidates);

        // 3. Rank layers / services. Metadata V2 graph access is optional because some
        // deployments (e.g. read-only raster servers, pre-provisioning test
        // harnesses) may not have it wired. Missing graph provider → empty dataset
        // list rather than a hard failure; the clarification envelope will
        // prompt for explicit inputs. Authorization and pin application
        // both run before the shortlist cap for the same reasons as
        // processes above.
        var datasetCandidates = await RankDatasetsAsync(request, cancellationToken).ConfigureAwait(false);
        datasetCandidates = _authorizationFilter.Filter(principal, datasetCandidates);
        datasetCandidates = ClarificationAnswerResolver.ApplyPin(
            datasetCandidates, applied.PinnedDatasetId, "dataset.selection", appliedIds);
        datasetCandidates = ApplyBandsAndCap(datasetCandidates);

        var ranking = new CandidateRanking
        {
            Datasets = datasetCandidates,
            Processes = processCandidates
        };

        // A `publish.source` answer that points at a service catalog entry
        // cannot be drafted — PublishSourceKind has no FeatureService value,
        // so labeling it FeatureLayer would leak an incorrect source kind.
        // Drop the applied-id and resolved-source override so the evaluator
        // re-emits `publish.source` and the drafter omits the `publishing`
        // block instead of producing a silently mislabeled draft. IntentDrafter
        // enforces the same invariant on the drafting side; the coordinated
        // drop here is what keeps the clarification envelope visible to the
        // caller.
        var resolvedPublishSourceId = applied.ResolvedPublishSourceId;
        if (!string.IsNullOrWhiteSpace(resolvedPublishSourceId)
            && MatchesServiceCandidate(resolvedPublishSourceId, ranking))
        {
            resolvedPublishSourceId = null;
            appliedIds.Remove("publish.source");
        }

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
        var intentId = string.IsNullOrWhiteSpace(request.IntentId)
            ? $"grounding-{Guid.NewGuid():N}"
            : request.IntentId;
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
            applied.PublishTargetOverride,
            resolvedPublishSourceId);

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
        // Direct-provider path for unit tests that inject a substituted
        // Metadata V2 graph provider without a scope factory.
        if (_metadataGraphProvider is not null)
        {
            return await ScoreDatasetsFromGraphAsync(_metadataGraphProvider, request, cancellationToken).ConfigureAwait(false);
        }

        // Production path: resolve the scoped graph provider inside a fresh
        // DI scope so database connections cycle per request instead of
        // being captured by the singleton GroundingService.
        if (_serviceScopeFactory is null)
        {
            return [];
        }

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var graphProvider = scope.ServiceProvider.GetService<IMetadataV2GraphProvider>();
        if (graphProvider is null)
        {
            return [];
        }

        return await ScoreDatasetsFromGraphAsync(graphProvider, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GroundingCandidate>> ScoreDatasetsFromGraphAsync(
        IMetadataV2GraphProvider graphProvider,
        GroundingRequest request,
        CancellationToken cancellationToken)
    {
        // An injected graph provider is the caller's contract that catalog metadata
        // exists. When it is present but the backing store fails, surface
        // GroundingException(CatalogUnavailable) so MCP returns a retryable
        // `unavailable` error (see docs/developer/MCP_SERVER.md error table)
        // instead of silently returning a successful empty ranking. The
        // no-provider-registered fallback in RankDatasetsAsync still returns
        // `[]` because that is a configuration shape, not an outage.
        MetadataV2GraphSnapshot snapshot;
        try
        {
            snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GroundingLog.PassRejected(_logger, nameof(GroundingErrorKind.CatalogUnavailable), ex.Message);
            throw new GroundingException(
                GroundingErrorKind.CatalogUnavailable,
                $"Metadata graph is unavailable: {ex.Message}",
                ex);
        }

        var layerCandidates = MetadataV2GroundingCatalog.BuildLayers(snapshot)
            .Select(l => new LayerCandidate(l.LayerId, l.Name, l.Description))
            .ToArray();
        var serviceCandidates = snapshot.Graph.Services
            .Select(s => new ServiceCandidate(s.Metadata.Name, s.Metadata.Description))
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
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
        merged.Sort(GroundingCandidateComparer.ByScoreDescending);
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

    private static void EnsureClarificationIntentIdMatches(GroundingRequest request)
    {
        if (request.ClarificationResponse is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.IntentId))
        {
            throw new GeoprocessingValidationException(
                "Clarification turn requires a non-empty intentId on the grounding request.");
        }

        if (string.IsNullOrWhiteSpace(request.ClarificationResponse.IntentId))
        {
            throw new GeoprocessingValidationException(
                "Clarification response is missing an intentId; it must identify the intent being clarified.");
        }

        if (!string.Equals(request.IntentId, request.ClarificationResponse.IntentId, StringComparison.Ordinal))
        {
            throw new GeoprocessingValidationException(
                $"Clarification response intentId '{request.ClarificationResponse.IntentId}' does not match "
                + $"request intentId '{request.IntentId}'. Clarification answers can only be applied to the "
                + "intent they were requested for.");
        }
    }

    private static bool MatchesServiceCandidate(string sourceId, CandidateRanking ranking)
    {
        foreach (var candidate in ranking.Datasets)
        {
            if (string.Equals(candidate.Id, sourceId, StringComparison.Ordinal)
                && candidate.DatasetSubtype == DatasetSubtype.Service)
            {
                return true;
            }
        }
        return false;
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
            // IntentConstraints.SpatialReferenceId is the AOI SRID, so it only
            // satisfies the generic single-SRID parameter (by convention named
            // `srid`). Directional parameters like `fromSrid`/`toSrid` on
            // `geometry.project` must still prompt a clarification because the
            // AOI SRID cannot disambiguate source vs. target projection.
            ProcessParameterValueType.Srid =>
                constraints.SpatialReferenceId.HasValue
                && string.Equals(parameter.Name, "srid", StringComparison.OrdinalIgnoreCase),
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
        // choices without a new contract surface. Sort by parameter name so
        // two semantically identical clarification payloads produce the same
        // assumption ordering regardless of the caller's key insertion order
        // — preserving the slice's stability guarantee.
        if (resolvedParameters.Count > 0)
        {
            var parameterNames = new string[resolvedParameters.Count];
            var index = 0;
            foreach (var name in resolvedParameters.Keys)
            {
                parameterNames[index++] = name;
            }
            Array.Sort(parameterNames, StringComparer.Ordinal);
            foreach (var name in parameterNames)
            {
                assumptions.Add($"param.{name}={resolvedParameters[name]}");
            }
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
