// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Server.Features.AiBuilder.Fixtures;
using Honua.Server.Features.NlQuery.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.NlQuery;

/// <summary>
/// Fixture-replay NL query plan provider. Matches the incoming prompt against
/// the deterministic AI-builder contract fixtures and returns the canned
/// <see cref="FilterPlan"/> the fixture promises for that prompt. Used to keep
/// platform-contract tests and demo flows reproducible without making live
/// model calls.
/// </summary>
/// <remarks>
/// Only scenarios that expose <c>draft.filterPlan</c> resolve to a successful
/// plan. Ambiguity, unsupported-capability, auth-denied, oversized, and
/// apply-failure scenarios surface as <see cref="NlQueryPlanResult.Failure"/>
/// with a structured reason — those non-success states belong to the grounding
/// and plan-validation layers, not the NL planner.
/// </remarks>
internal sealed class DeterministicNlQueryPlanProvider : INlQueryPlanProvider
{
    private readonly Dictionary<string, FilterPlan> _plansByPrompt;
    private readonly Dictionary<string, string> _nonSuccessByPrompt;
    private readonly ILogger<DeterministicNlQueryPlanProvider> _logger;

    public DeterministicNlQueryPlanProvider(
        AiBuilderFixtureCatalog catalog,
        ILogger<DeterministicNlQueryPlanProvider> logger)
    {
        _logger = logger;

        var plans = new Dictionary<string, FilterPlan>(StringComparer.Ordinal);
        var nonSuccess = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var scenario in catalog.Scenarios)
        {
            var key = AiBuilderFixtureCatalog.Normalize(scenario.Prompt);
            if (TryReadFilterPlan(scenario.Root, out var plan))
            {
                // Last writer wins so cache-hit scenarios that share a prompt
                // with the originating success scenario both replay the same
                // filter plan (which is the whole point of the cache-hit case).
                plans[key] = plan!;
                continue;
            }

            if (!nonSuccess.ContainsKey(key))
            {
                nonSuccess[key] = scenario.Case;
            }
        }

        _plansByPrompt = plans;
        _nonSuccessByPrompt = nonSuccess;
    }

    /// <inheritdoc />
    public Task<NlQueryPlanResult> GeneratePlanAsync(
        NlQueryPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.nlquery.generate-plan");
        activity?.SetTag("nl.provider", "deterministic");
        activity?.SetTag("nl.collection", request.CollectionId);

        NlQueryLog.PlanRequested(_logger, request.CollectionId ?? "unknown", "deterministic-fixture");

        var key = AiBuilderFixtureCatalog.Normalize(request.Query ?? string.Empty);
        if (_plansByPrompt.TryGetValue(key, out var plan))
        {
            NlQueryLog.PlanSucceeded(_logger, request.CollectionId ?? "unknown", plan.Clauses.Length);
            activity?.SetTag("nl.success", true);
            activity?.SetTag("nl.clause_count", plan.Clauses.Length);
            return Task.FromResult(NlQueryPlanResult.Success(plan));
        }

        if (_nonSuccessByPrompt.TryGetValue(key, out var caseName))
        {
            var reason = $"Prompt mapped to fixture case '{caseName}' which does not yield a filter plan.";
            NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", reason);
            activity?.SetTag("nl.success", false);
            activity?.SetTag("nl.fixture_case", caseName);
            return Task.FromResult(NlQueryPlanResult.Failure(reason));
        }

        const string Unknown = "No deterministic fixture entry matches the supplied prompt.";
        NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", Unknown);
        activity?.SetTag("nl.success", false);
        return Task.FromResult(NlQueryPlanResult.Failure(Unknown));
    }

    private static bool TryReadFilterPlan(JsonElement scenario, out FilterPlan? plan)
    {
        plan = null;
        if (!scenario.TryGetProperty("draft", out var draft)
            || draft.ValueKind != JsonValueKind.Object
            || !draft.TryGetProperty("filterPlan", out var filterPlan)
            || filterPlan.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        plan = filterPlan.Deserialize(NlQueryJsonContext.Default.FilterPlan);
        return plan is not null;
    }
}
