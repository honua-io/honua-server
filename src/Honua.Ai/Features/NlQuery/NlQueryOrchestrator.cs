// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Features.NlQuery.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.NlQuery;

/// <summary>
/// Orchestrates the end-to-end NL query pipeline: provider invocation → plan compilation → filter AST.
/// </summary>
internal sealed class NlQueryOrchestrator(
    INlQueryPlanProvider planProvider,
    ILogger<NlQueryOrchestrator> logger) : INlQueryOrchestrator
{
    /// <inheritdoc />
    public async Task<NlQueryOrchestrationResult> ExecuteAsync(
        NlQueryPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("NlQuery.Orchestrate");
        activity?.SetTag("nl_query.collection", request.CollectionId ?? "unknown");
        activity?.SetTag("nl_query.query_length", request.Query.Length);

        var collectionId = request.CollectionId ?? "unknown";

        // Step 1: Generate the filter plan from the NL provider.
        var planResult = await planProvider.GeneratePlanAsync(request, cancellationToken);

        if (!planResult.IsSuccess || planResult.Plan is null)
        {
            var reason = planResult.ErrorMessage ?? "Unknown plan generation error";
            NlQueryLog.PlanFailed(logger, collectionId, reason);
            activity?.SetTag("nl_query.status", "plan_failed");
            return NlQueryOrchestrationResult.PlanGenerationFailed(reason);
        }

        NlQueryLog.PlanSucceeded(logger, collectionId, planResult.Plan.Clauses.Length);

        // Step 2: Compile the filter plan into a FilterExpression AST.
        var compileResult = FilterPlanCompiler.Compile(planResult.Plan, request.Resource);

        if (!compileResult.IsSuccess || compileResult.Expression is null)
        {
            var reason = compileResult.ErrorMessage ?? "Unknown compilation error";
            NlQueryLog.CompilationFailed(logger, collectionId, reason);
            activity?.SetTag("nl_query.status", "compile_failed");
            return NlQueryOrchestrationResult.CompilationFailed(reason, planResult.Plan);
        }

        NlQueryLog.CompilationSucceeded(logger, collectionId);
        activity?.SetTag("nl_query.status", "success");
        activity?.SetTag("nl_query.clause_count", planResult.Plan.Clauses.Length);

        return NlQueryOrchestrationResult.Success(compileResult.Expression, planResult.Plan);
    }
}
