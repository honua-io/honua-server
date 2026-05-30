// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Ai.Grounding;

/// <summary>
/// Default deterministic grounding engine. Ranks candidates using a weighted
/// bag-of-lemma overlap against catalog text, and classifies workflow family
/// via a small hand-coded verb lexicon. Purely CPU, no external calls, safe
/// for Native AOT.
/// </summary>
internal sealed class DeterministicGroundingEngine : IGroundingEngine
{
    public string Name => "deterministic";

    /// <summary>
    /// Verb → family mapping. Picking this over a confidence score table
    /// keeps the classifier trivially auditable; adding a token to one of
    /// these sets is a conscious taxonomy decision.
    /// </summary>
    private static readonly FrozenDictionary<string, WorkflowFamily> FamilyVerbs =
        new Dictionary<string, WorkflowFamily>(StringComparer.Ordinal)
        {
            // Analyze
            ["analyz"] = WorkflowFamily.Analyze,
            ["analys"] = WorkflowFamily.Analyze,
            ["buffer"] = WorkflowFamily.Analyze,
            ["cluster"] = WorkflowFamily.Analyze,
            ["join"] = WorkflowFamily.Analyze,
            ["intersect"] = WorkflowFamily.Analyze,
            ["density"] = WorkflowFamily.Analyze,
            ["dissolv"] = WorkflowFamily.Analyze,
            ["simplif"] = WorkflowFamily.Analyze,
            ["union"] = WorkflowFamily.Analyze,
            ["measur"] = WorkflowFamily.Analyze,
            ["calculat"] = WorkflowFamily.Analyze,
            ["count"] = WorkflowFamily.Analyze,
            ["nearest"] = WorkflowFamily.Analyze,
            ["distanc"] = WorkflowFamily.Analyze,
            ["detect"] = WorkflowFamily.Analyze,
            ["identif"] = WorkflowFamily.Analyze,
            ["classif"] = WorkflowFamily.Analyze,
            ["aggregat"] = WorkflowFamily.Analyze,
            ["clip"] = WorkflowFamily.Analyze,
            ["project"] = WorkflowFamily.Analyze,

            // Publish data
            ["publish"] = WorkflowFamily.PublishData,
            ["expos"] = WorkflowFamily.PublishData,
            ["host"] = WorkflowFamily.PublishData,
            ["featureserver"] = WorkflowFamily.PublishData,
            ["tileserver"] = WorkflowFamily.PublishData,
            ["share"] = WorkflowFamily.PublishData,
            ["export"] = WorkflowFamily.PublishData,
            ["releas"] = WorkflowFamily.PublishData,
            ["endpoint"] = WorkflowFamily.PublishData,
            ["servic"] = WorkflowFamily.PublishData,

            // Build app
            ["app"] = WorkflowFamily.BuildApp,
            ["dashboard"] = WorkflowFamily.BuildApp,
            ["page"] = WorkflowFamily.BuildApp,
            ["view"] = WorkflowFamily.BuildApp,
            ["portal"] = WorkflowFamily.BuildApp,
            ["widget"] = WorkflowFamily.BuildApp,
            ["bundl"] = WorkflowFamily.BuildApp,
            ["webapp"] = WorkflowFamily.BuildApp,
            ["viewer"] = WorkflowFamily.BuildApp,
            ["builder"] = WorkflowFamily.BuildApp,

            // Automate deploy
            ["deploy"] = WorkflowFamily.AutomateDeploy,
            ["schedul"] = WorkflowFamily.AutomateDeploy,
            ["pipelin"] = WorkflowFamily.AutomateDeploy,
            ["cron"] = WorkflowFamily.AutomateDeploy,
            ["promot"] = WorkflowFamily.AutomateDeploy,
            ["rollout"] = WorkflowFamily.AutomateDeploy,
            ["environ"] = WorkflowFamily.AutomateDeploy,
            ["tenant"] = WorkflowFamily.AutomateDeploy,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public WorkflowFamilyClassification Classify(GroundingRequest request)
    {
        if (request.WorkflowFamilyHint.HasValue)
        {
            return new WorkflowFamilyClassification
            {
                Value = request.WorkflowFamilyHint.Value,
                Confidence = 1.0,
                Evidence = ["hint"]
            };
        }

        var tokens = GroundingTokenizer.Tokenize(request.Goal);
        if (tokens.Count == 0)
        {
            return new WorkflowFamilyClassification
            {
                Value = WorkflowFamily.Analyze,
                Confidence = 0.0,
                Evidence = ["empty"]
            };
        }

        Span<int> counts = stackalloc int[4];
        var evidence = new List<string>(capacity: 4);

        foreach (var token in tokens)
        {
            foreach (var pair in FamilyVerbs)
            {
                if (token.StartsWith(pair.Key, StringComparison.Ordinal))
                {
                    counts[(int)pair.Value]++;
                    evidence.Add($"verb:{token}→{pair.Value}");
                    break;
                }
            }
        }

        var total = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            total += counts[i];
        }

        if (total == 0)
        {
            // No recognized verb — fall back to Analyze (the most common
            // workflow) with a deliberately low confidence so the service
            // surfaces a LowConfidence clarification.
            return new WorkflowFamilyClassification
            {
                Value = WorkflowFamily.Analyze,
                Confidence = 0.20,
                Evidence = ["fallback:analyze"]
            };
        }

        var winner = 0;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > counts[winner])
            {
                winner = i;
            }
        }

        var winnerCount = counts[winner];
        var confidence = total == 0 ? 0.0 : (double)winnerCount / total;
        return new WorkflowFamilyClassification
        {
            Value = (WorkflowFamily)winner,
            Confidence = Math.Round(confidence, 3),
            Evidence = evidence
        };
    }

    public IReadOnlyList<GroundingCandidate> ScoreProcesses(
        GroundingRequest request,
        IReadOnlyList<ProcessDefinition> processes)
    {
        var tokens = GroundingTokenizer.TokenizeToSet(request.Goal);
        if (tokens.Count == 0 || processes.Count == 0)
        {
            return [];
        }

        var results = new List<GroundingCandidate>(processes.Count);
        foreach (var process in processes)
        {
            var score = ScoreProcess(process, tokens, out var evidence);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new GroundingCandidate
            {
                Id = process.ProcessId,
                Kind = CandidateKind.Process,
                DisplayName = process.Title,
                Score = Math.Round(score, 3),
                ConfidenceBand = ConfidenceBand.Low, // corrected by service using options
                Evidence = evidence
            });
        }

        results.Sort(GroundingCandidateComparer.ByScoreDescending);
        return results;
    }

    public IReadOnlyList<GroundingCandidate> ScoreLayers(
        GroundingRequest request,
        IReadOnlyList<LayerCandidate> layers)
    {
        var tokens = GroundingTokenizer.TokenizeToSet(request.Goal);
        if (tokens.Count == 0 || layers.Count == 0)
        {
            return [];
        }

        var results = new List<GroundingCandidate>(layers.Count);
        foreach (var layer in layers)
        {
            var score = ScoreText(layer.Name, layer.Description, tokens, out var evidence);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new GroundingCandidate
            {
                Id = layer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Kind = CandidateKind.Dataset,
                DatasetSubtype = DatasetSubtype.Layer,
                DisplayName = layer.Name,
                Score = Math.Round(score, 3),
                ConfidenceBand = ConfidenceBand.Low,
                Evidence = evidence
            });
        }

        results.Sort(GroundingCandidateComparer.ByScoreDescending);
        return results;
    }

    public IReadOnlyList<GroundingCandidate> ScoreServices(
        GroundingRequest request,
        IReadOnlyList<ServiceCandidate> services)
    {
        var tokens = GroundingTokenizer.TokenizeToSet(request.Goal);
        if (tokens.Count == 0 || services.Count == 0)
        {
            return [];
        }

        var results = new List<GroundingCandidate>(services.Count);
        foreach (var service in services)
        {
            var score = ScoreText(service.Name, service.Description, tokens, out var evidence);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new GroundingCandidate
            {
                Id = service.Name,
                Kind = CandidateKind.Dataset,
                DatasetSubtype = DatasetSubtype.Service,
                DisplayName = service.Name,
                Score = Math.Round(score, 3),
                ConfidenceBand = ConfidenceBand.Low,
                Evidence = evidence
            });
        }

        results.Sort(GroundingCandidateComparer.ByScoreDescending);
        return results;
    }

    private static double ScoreProcess(
        ProcessDefinition process,
        FrozenSet<string> queryTokens,
        out IReadOnlyList<string> evidence)
    {
        // Title matches carry the most weight because operators usually name
        // the operation (e.g. "buffer the roads"). Description matches fill
        // in when the operator paraphrases. Parameter-name matches are weak
        // but prevent category confusion (e.g. "buffer by distance" raises
        // the buffer process over density-binning even though both mention
        // "distance").
        var titleHits = CountOverlap(queryTokens, process.Title);
        var descriptionHits = CountOverlap(queryTokens, process.Description);
        var categoryHit = CountOverlap(queryTokens, process.Category);
        var paramHits = 0;
        foreach (var parameter in process.Parameters)
        {
            paramHits += CountOverlap(queryTokens, parameter.Name)
                + CountOverlap(queryTokens, parameter.DisplayName);
        }

        if (titleHits + descriptionHits + categoryHit + paramHits == 0)
        {
            evidence = [];
            return 0.0;
        }

        // Weighted sum normalized to [0,1] by the maximum possible weight
        // contribution for a single token. Keeps the score interpretable.
        var raw = (titleHits * 0.55) + (categoryHit * 0.20) + (descriptionHits * 0.15) + (paramHits * 0.10);
        var score = Math.Min(1.0, raw / Math.Max(1.0, queryTokens.Count * 0.55));

        var ev = new List<string>(capacity: 4);
        if (titleHits > 0)
        {
            ev.Add($"title:{titleHits}");
        }
        if (categoryHit > 0)
        {
            ev.Add($"category:{categoryHit}");
        }
        if (descriptionHits > 0)
        {
            ev.Add($"description:{descriptionHits}");
        }
        if (paramHits > 0)
        {
            ev.Add($"parameter:{paramHits}");
        }

        evidence = ev;
        return score;
    }

    private static double ScoreText(
        string? primary,
        string? secondary,
        FrozenSet<string> queryTokens,
        out IReadOnlyList<string> evidence)
    {
        var primaryHits = CountOverlap(queryTokens, primary);
        var secondaryHits = CountOverlap(queryTokens, secondary);

        if (primaryHits + secondaryHits == 0)
        {
            evidence = [];
            return 0.0;
        }

        var raw = (primaryHits * 0.70) + (secondaryHits * 0.30);
        var score = Math.Min(1.0, raw / Math.Max(1.0, queryTokens.Count * 0.70));

        var ev = new List<string>(capacity: 2);
        if (primaryHits > 0)
        {
            ev.Add($"name:{primaryHits}");
        }
        if (secondaryHits > 0)
        {
            ev.Add($"description:{secondaryHits}");
        }

        evidence = ev;
        return score;
    }

    private static int CountOverlap(FrozenSet<string> queryTokens, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var hits = 0;
        foreach (var token in GroundingTokenizer.Tokenize(text))
        {
            if (queryTokens.Contains(token))
            {
                hits++;
            }
        }

        return hits;
    }
}
