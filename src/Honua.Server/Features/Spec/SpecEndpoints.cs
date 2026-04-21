// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Server.Features.Spec.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Spec;

/// <summary>
/// Maps the Terraform-style <c>/v1/spec/*</c> endpoints:
/// <list type="bullet">
///   <item><c>POST /v1/spec/plan</c> — returns the DAG, cost estimates, and warnings.</item>
///   <item><c>POST /v1/spec/apply</c> — streams per-node apply events over SSE.</item>
///   <item><c>POST /v1/spec/cancel</c> — cooperatively cancels an in-flight run.</item>
///   <item><c>GET /v1/spec/artifact/{hash}</c> — retrieves a cached artifact by hash.</item>
/// </list>
/// </summary>
internal static class SpecEndpoints
{
    public static IEndpointRouteBuilder MapSpecEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/spec")
            .WithTags("Spec");

        group.MapPost("/plan", HandlePlanAsync)
            .WithDisplayName("Spec Plan")
            .WithName("SpecPlan")
            .WithDescription("Compiles a canonical spec document into a plan containing the DAG, per-node cost estimates, and structured warnings.")
            .Produces<SpecPlanResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/apply", HandleApplyAsync)
            .WithDisplayName("Spec Apply")
            .WithName("SpecApply")
            .WithDescription("Applies a canonical spec document and streams per-node events over SSE. Send Accept: text/event-stream.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/cancel", HandleCancelAsync)
            .WithDisplayName("Spec Cancel")
            .WithName("SpecCancel")
            .WithDescription("Cooperatively cancels an in-flight apply run by apply token.")
            .Produces<SpecCancelResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/artifact/{hash}", HandleArtifactAsync)
            .WithDisplayName("Spec Artifact")
            .WithName("SpecArtifact")
            .WithDescription("Retrieves a cached artifact by content hash.")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandlePlanAsync(
        HttpContext context,
        ISpecPlanner planner,
        CancellationToken cancellationToken)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(Honua.Core.Features.Spec.Services.SpecTelemetry.PlanActivityName);

        var request = await TryReadRequestAsync(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                SpecDiagnosticCodes.InvalidRequestBody,
                "Request body could not be parsed as a spec document.");
        }

        var document = ToDocument(request);
        var plan = await planner.PlanAsync(document, cancellationToken).ConfigureAwait(false);

        activity?.SetTag("honua.spec.plan_id", plan.PlanId);
        activity?.SetTag("honua.spec.total_nodes", plan.Nodes.Count);

        if (plan.Warnings.Any(w => w.Severity == SpecDiagnosticSeverity.Error))
        {
            var first = plan.Warnings.First(w => w.Severity == SpecDiagnosticSeverity.Error);
            return Results.Json(
                new SpecProblem
                {
                    Type = "urn:honua:spec:plan-error",
                    Title = "Spec plan rejected",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = first.Message,
                    Code = first.Code,
                    NodeId = first.NodeId,
                    Remedy = first.Remedy
                },
                SpecJsonContext.Default.SpecProblem,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(ToResponse(plan), SpecJsonContext.Default.SpecPlanResponse);
    }

    private static async Task<IResult> HandleApplyAsync(
        HttpContext context,
        ISpecApplyEngine engine,
        CancellationToken cancellationToken)
    {
        var accept = context.Request.Headers.Accept.ToString();
        if (!accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                "accept-required",
                "POST /v1/spec/apply requires Accept: text/event-stream.");
        }

        var request = await TryReadRequestAsync(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                SpecDiagnosticCodes.InvalidRequestBody,
                "Request body could not be parsed as a spec document.");
        }

        var document = ToDocument(request);
        var options = new SpecApplyOptions
        {
            CacheMode = request.CacheMode ?? SpecCacheMode.ReadWrite,
            MaxConcurrency = request.MaxConcurrency is int m && m > 0 ? m : 4
        };

        SpecApplyHandle handle;
        try
        {
            // Request cancellation only scopes the plan phase and the initial
            // handshake. Once StartAsync returns, the apply owns its own CTS.
            handle = await engine.StartAsync(document, options, cancellationToken).ConfigureAwait(false);
        }
        catch (SpecDocumentInvalidException invalid)
        {
            var primary = invalid.PrimaryDiagnostic;
            return Results.Json(
                new SpecProblem
                {
                    Type = $"urn:honua:spec:{primary.Code}",
                    Title = "Spec document rejected",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = primary.Message,
                    Code = primary.Code,
                    NodeId = primary.NodeId,
                    Remedy = primary.Remedy
                },
                SpecJsonContext.Default.SpecProblem,
                statusCode: StatusCodes.Status400BadRequest);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.Headers["X-Spec-Apply-Token"] = handle.ApplyToken;

        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        try
        {
            await foreach (var evt in handle.Events.WithCancellation(context.RequestAborted).ConfigureAwait(false))
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(evt, SpecJsonContext.Default.SpecApplyEvent);
                await context.Response.WriteAsync("id: ", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync(
                    evt.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync("\nevent: ", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync(evt.Kind.ToString(), context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync("\ndata: ", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(data, context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync("\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; apply continues until cancelled explicitly.
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleCancelAsync(
        HttpContext context,
        ISpecApplyEngine engine,
        CancellationToken cancellationToken)
    {
        SpecCancelRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                SpecJsonContext.Default.SpecCancelRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                "apply-token-missing",
                "Body could not be parsed; expected '{ \"applyToken\": \"<id>\" }'.");
        }

        var token = payload?.ApplyToken;
        if (string.IsNullOrEmpty(token))
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                "apply-token-missing",
                "Body must contain an 'applyToken' field.");
        }

        var cancelled = engine.TryCancel(token);
        if (!cancelled)
        {
            return Results.Json(
                new SpecProblem
                {
                    Type = "urn:honua:spec:apply-token-unknown",
                    Title = "Unknown apply token",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"No apply run is registered for token '{token}'.",
                    Code = SpecDiagnosticCodes.ApplyTokenUnknown
                },
                SpecJsonContext.Default.SpecProblem,
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new SpecCancelResponse
        {
            ApplyToken = token,
            Cancelled = true
        }, SpecJsonContext.Default.SpecCancelResponse);
    }

    private static async Task<IResult> HandleArtifactAsync(
        HttpContext context,
        string hash,
        IContentHashArtifactCache cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                "artifact-hash-missing",
                "Content hash is required.");
        }

        var reference = await cache.TryGetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return Results.Json(
                new SpecProblem
                {
                    Type = "urn:honua:spec:artifact-not-found",
                    Title = "Artifact not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Artifact '{hash}' is unknown or has been evicted.",
                    Code = SpecDiagnosticCodes.ArtifactNotFound
                },
                SpecJsonContext.Default.SpecProblem,
                statusCode: StatusCodes.Status404NotFound);
        }

        var stream = await cache.OpenReadAsync(hash, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return Results.Json(
                new SpecProblem
                {
                    Type = "urn:honua:spec:artifact-not-found",
                    Title = "Artifact evicted",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Artifact '{hash}' was evicted during retrieval.",
                    Code = SpecDiagnosticCodes.ArtifactNotFound
                },
                SpecJsonContext.Default.SpecProblem,
                statusCode: StatusCodes.Status404NotFound);
        }

        var contentType = reference.ContentType ?? "application/octet-stream";
        context.Response.Headers["X-Spec-Content-Hash"] = reference.ContentHash;
        return Results.Stream(stream, contentType);
    }

    private static async Task<SpecDocumentRequest?> TryReadRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                SpecJsonContext.Default.SpecDocumentRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CanonicalSpecDocument ToDocument(SpecDocumentRequest request)
    {
        var nodes = new List<CanonicalSpecNode>(request.Nodes.Count);
        foreach (var n in request.Nodes)
        {
            nodes.Add(new CanonicalSpecNode
            {
                Id = n.Id,
                Kind = n.Kind,
                Op = n.Op,
                Inputs = n.Inputs ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Parameters = n.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                CanonicalFragment = n.CanonicalFragment,
                SourcePins = n.SourcePins ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Nondeterministic = n.Nondeterministic
            });
        }

        return new CanonicalSpecDocument
        {
            GrammarVersion = request.GrammarVersion,
            ProcessFamilyVersion = request.ProcessFamilyVersion,
            SpecId = request.SpecId,
            Nodes = nodes
        };
    }

    private static SpecPlanResponse ToResponse(SpecPlan plan)
    {
        var nodes = new List<SpecPlanNodeResponse>(plan.Nodes.Count);
        foreach (var n in plan.Nodes)
        {
            nodes.Add(new SpecPlanNodeResponse
            {
                NodeId = n.NodeId,
                Kind = n.Kind,
                Op = n.Op,
                DependsOn = n.DependsOn,
                ContentHash = n.ContentHash,
                Cost = n.Cost,
                Warnings = n.Warnings
            });
        }

        return new SpecPlanResponse
        {
            PlanId = plan.PlanId,
            GrammarVersion = plan.GrammarVersion,
            ProcessFamilyVersion = plan.ProcessFamilyVersion,
            Nodes = nodes,
            Warnings = plan.Warnings
        };
    }

    private static IResult BuildProblem(int status, string code, string detail)
    {
        return Results.Json(
            new SpecProblem
            {
                Type = $"urn:honua:spec:{code}",
                Title = "Spec request rejected",
                Status = status,
                Detail = detail,
                Code = code
            },
            SpecJsonContext.Default.SpecProblem,
            statusCode: status);
    }
}
