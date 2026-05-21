// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Server.Features.Spec.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using SpecCanonicalJsonReader = Honua.Core.Features.Spec.Canonical.SpecJsonReader;

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
            .WithTags("Spec")
            // Spec is a Terraform-style developer-experience surface that is
            // currently anonymous by design: the apply engine performs its own
            // operator-scoped authorization for each node, and the public
            // entry points (validate/plan/apply/cancel/artifact) intentionally
            // expose the workflow to unauthenticated callers in early access.
            // Explicit AllowAnonymous documents that decision for
            // authorization-policy tooling and the audit guard
            // (Honua.Architecture.Tests.EndpointAuthorizationGuardTests).
            // TODO(#1144): tighten to RequireAdminAuthorization once the SDK
            // and test harness route through an authenticated transport.
            .AllowAnonymous();

        group.MapPost("/validate", HandleValidateAsync)
            .WithDisplayName("Spec Validate")
            .WithName("SpecValidate")
            .WithDescription("Parses and validates spec DSL or canonical JSON, returning structured diagnostics and optional canonical JSON.")
            .Produces<SpecValidateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

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

    private static async Task<IResult> HandleValidateAsync(
        HttpContext context,
        ISpecParser parser,
        ISpecValidator validator,
        ISpecCanonicalizer canonicalizer,
        CancellationToken cancellationToken)
    {
        var request = await TryReadValidateRequestAsync(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                SpecDiagnosticCodes.InvalidRequestBody,
                "Request body could not be parsed as a spec validation request.");
        }

        var hasText = !string.IsNullOrWhiteSpace(request.Text);
        var hasSpec = request.Spec.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        if (hasText == hasSpec)
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                SpecDiagnosticCodes.InvalidRequestBody,
                "Body must contain exactly one of 'text' or 'spec'.");
        }

        if (hasSpec && request.Spec.ValueKind != JsonValueKind.Object)
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                SpecDiagnosticCodes.InvalidRequestBody,
                "'spec' must be a canonical JSON object.");
        }

        var diagnostics = new List<SpecDiagnostic>();
        SpecDocument? document = null;

        if (hasText)
        {
            var parse = parser.Parse(request.Text!);
            diagnostics.AddRange(parse.Diagnostics);
            document = parse.Document;
        }
        else
        {
            try
            {
                document = SpecCanonicalJsonReader.Read(request.Spec.GetRawText());
            }
            catch (JsonException ex)
            {
                diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    $"Canonical spec JSON could not be parsed: {ex.Message}",
                    SourceSpan.Synthetic));
            }
            catch (InvalidOperationException ex)
            {
                diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    $"Canonical spec JSON could not be read: {ex.Message}",
                    SourceSpan.Synthetic));
            }
            catch (FormatException ex)
            {
                diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    $"Canonical spec JSON could not be read: {ex.Message}",
                    SourceSpan.Synthetic));
            }
        }

        if (document is not null && !HasErrorDiagnostics(diagnostics))
        {
            var validation = validator.Validate(document);
            diagnostics.AddRange(validation.Diagnostics);
        }

        var isValid = document is not null && !HasErrorDiagnostics(diagnostics);
        var canonicalJson = isValid && request.IncludeCanonicalJson
            ? canonicalizer.ToJson(document!, indent: false)
            : null;

        return Results.Json(new SpecValidateResponse
        {
            IsValid = isValid,
            Diagnostics = diagnostics.Select(ToValidateDiagnostic).ToList(),
            CanonicalJson = canonicalJson,
            Grammar = document?.Grammar,
            OperatorCapabilityVersion = SpecGrammarVersion.CurrentOperatorCapability
        }, SpecJsonContext.Default.SpecValidateResponse);
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

        CanonicalSpecDocument document;
        try
        {
            document = ToDocument(request);
        }
        catch (SpecDocumentInvalidException invalid)
        {
            return ProblemFromInvalid(invalid);
        }

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

        CanonicalSpecDocument document;
        try
        {
            document = ToDocument(request);
        }
        catch (SpecDocumentInvalidException invalid)
        {
            return ProblemFromInvalid(invalid);
        }

        // Mirror the `kind` whitelist: null means "omitted" (default ReadWrite),
        // but JsonStringEnumConverter with UseStringEnumConverter still accepts
        // numeric values, so `"cacheMode": 999` would otherwise flow through as
        // an undefined enum and silently default to ReadWrite via the gRPC-side
        // mapping's catch-all arm. Reject invalid enums at the boundary with a
        // stable diagnostic code.
        if (request.CacheMode is SpecCacheMode cacheMode && !IsDefinedCacheMode(cacheMode))
        {
            return BuildProblem(StatusCodes.Status400BadRequest,
                SpecDiagnosticCodes.UnknownCacheMode,
                "Request 'cacheMode' must be one of: ReadWrite, ReadOnly, Bypass.");
        }

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
            return ProblemFromInvalid(invalid);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.Headers["X-Spec-Apply-Token"] = handle.ApplyToken;

        try
        {
            // Handshake flush sits inside the disconnect guard: if the client
            // drops after headers are sent but before the first event frame,
            // the RequestAborted token trips here and we must treat it the
            // same as a mid-stream disconnect — swallow the OCE and let the
            // background apply continue until cancelled explicitly.
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

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

    private static async Task<SpecValidateRequest?> TryReadValidateRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                SpecJsonContext.Default.SpecValidateRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CanonicalSpecDocument ToDocument(SpecDocumentRequest request)
    {
        // `SpecDocumentRequest.Nodes` defaults to an empty list, but System.Text.Json
        // preserves an explicit `"nodes": null` as a genuine null. Treat that
        // and any null node entry as a structural problem with the canonical
        // document rather than letting the enumeration NRE later.
        var requestNodes = request.Nodes;
        if (requestNodes is null)
        {
            throw new SpecDocumentInvalidException(new[]
            {
                new SpecWarning
                {
                    Code = SpecDiagnosticCodes.InvalidRequestBody,
                    Message = "Request body is missing the 'nodes' collection.",
                    Severity = SpecDiagnosticSeverity.Error,
                    Remedy = "Provide a 'nodes' array (may be empty, but must not be null)."
                }
            });
        }

        // Reject nodes with an omitted or unrecognised kind at the transport
        // boundary rather than defaulting them to Compute (see finding:
        // unknown-kind). JsonStringEnumConverter still accepts numeric values,
        // so a client sending "kind": 999 would otherwise slip past the null
        // check and reach the orchestrator as an undefined enum. Collect all
        // offenders so an operator fixing a large document sees the full list
        // in one round-trip.
        List<SpecWarning>? fatal = null;
        for (var i = 0; i < requestNodes.Count; i++)
        {
            var n = requestNodes[i];
            if (n is null)
            {
                fatal ??= new List<SpecWarning>();
                fatal.Add(new SpecWarning
                {
                    Code = SpecDiagnosticCodes.InvalidNodeId,
                    Message = $"Node at index {i} is null.",
                    Severity = SpecDiagnosticSeverity.Error,
                    Remedy = "Remove the null entry or replace it with a populated node object."
                });
                continue;
            }

            if (n.Kind is null || !IsDefinedResourceKind(n.Kind.Value))
            {
                fatal ??= new List<SpecWarning>();
                fatal.Add(new SpecWarning
                {
                    Code = SpecDiagnosticCodes.UnknownKind,
                    Message = n.Kind is null
                        ? $"Node '{n.Id}' does not declare a resource kind."
                        : $"Node '{n.Id}' declares unrecognised resource kind.",
                    Severity = SpecDiagnosticSeverity.Error,
                    NodeId = n.Id,
                    Remedy = "Set 'kind' to one of: Compute, Report, Dataset, Service, App."
                });
            }
        }

        if (fatal is not null)
        {
            throw new SpecDocumentInvalidException(fatal);
        }

        var nodes = new List<CanonicalSpecNode>(requestNodes.Count);
        foreach (var n in requestNodes)
        {
            nodes.Add(new CanonicalSpecNode
            {
                Id = n.Id,
                Kind = n.Kind!.Value,
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

    private static SpecValidateDiagnosticDto ToValidateDiagnostic(SpecDiagnostic diagnostic)
        => new()
        {
            Code = diagnostic.Code.ToString(),
            Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            Message = diagnostic.Message,
            Path = diagnostic.Path,
            Span = diagnostic.Span.HasLocation
                ? new SpecSourceSpanDto
                {
                    Line = diagnostic.Span.Line,
                    Column = diagnostic.Span.Column,
                    Offset = diagnostic.Span.Offset,
                    Length = diagnostic.Span.Length
                }
                : null
        };

    private static bool HasErrorDiagnostics(IEnumerable<SpecDiagnostic> diagnostics)
        => diagnostics.Any(diagnostic => diagnostic.Severity == SpecDiagnosticSeverity.Error);

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

    // AOT-safe whitelist for inbound resource kinds. Kept in sync with the
    // gRPC-side mapping in SpecProtoMapping.IsDefinedResourceKind and the
    // CanonicalSpecNode.Kind surface — add a case when a new kind is landed.
    private static bool IsDefinedResourceKind(SpecResourceKind kind) => kind switch
    {
        SpecResourceKind.Compute => true,
        SpecResourceKind.Report => true,
        SpecResourceKind.Dataset => true,
        SpecResourceKind.Service => true,
        SpecResourceKind.App => true,
        _ => false
    };

    private static bool IsDefinedCacheMode(SpecCacheMode mode) => mode switch
    {
        SpecCacheMode.ReadWrite => true,
        SpecCacheMode.ReadOnly => true,
        SpecCacheMode.Bypass => true,
        _ => false
    };

    private static IResult ProblemFromInvalid(SpecDocumentInvalidException invalid)
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
}
