// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Geoprocessing.Release;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// GP process release and binding admin API (honua-server#6221): publish an immutable release
/// lock, read releases, read the live binding of a process together with what this server can
/// execute, and activate (deploy or roll back) a release under an expected binding version.
/// This is the seam honua-release's <c>gp-deploy-process.yml</c> calls after the worker path is
/// ready; every refusal is returned as data so the deployment receipt can record it.
/// </summary>
public static class GpProcessReleaseEndpoints
{
    private const string ScheduleReconciliationNote =
        "pending-orchestration-integration: activation does not create, modify, or duplicate schedules.";

    /// <summary>Maps the admin routes under <c>/api/v1/admin/gp/process-releases</c>.</summary>
    public static void MapGpProcessReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/gp/process-releases")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Geoprocessing")
            .RequireAdminAuthorization();

        group.MapPost("", HandlePublishRelease)
            .WithDisplayName("Publish GP Process Release")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Accepts<string>("application/json")
            .Produces<GpProcessReleaseResponse>(StatusCodes.Status201Created)
            .Produces<GpProcessReleaseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("", HandleListReleases)
            .WithDisplayName("List GP Process Releases")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<GpProcessReleaseListResponse>();

        group.MapGet("/bindings", HandleListBindings)
            .WithDisplayName("List GP Process Bindings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<GpProcessBindingListResponse>();

        group.MapGet("/bindings/{process}", HandleGetBinding)
            .WithDisplayName("Get GP Process Binding")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<GpProcessBindingResponse>();

        group.MapPost("/bindings/{process}/activate", HandleActivateBinding)
            .WithDisplayName("Activate GP Process Release")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<GpProcessBindingResponse>()
            .Produces<GpProcessBindingRefusedResponse>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{digest}", HandleGetRelease)
            .WithDisplayName("Get GP Process Release")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<string>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandlePublishRelease(
        [FromServices] IGpProcessReleaseStore store,
        HttpContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        GpProcessReleaseLock lockDocument;
        try
        {
            lockDocument = GpProcessReleaseLock.Parse(body);
        }
        catch (GpProcessReleaseLockException ex)
        {
            return Results.Problem(
                title: "The body is not a valid honua.gp-process-release-lock/v1 document.",
                detail: string.Join("; ", ex.Errors),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (record, created) = await store.PublishWithStatusAsync(lockDocument, ActorOf(context), context.RequestAborted).ConfigureAwait(false);
        var response = ToResponse(record, created);
        return created
            ? Results.Created($"/api/v1/admin/gp/process-releases/{record.Digest}", response)
            : Results.Ok(response);
    }

    private static async Task<IResult> HandleListReleases(
        [FromServices] IGpProcessReleaseStore store,
        [FromQuery] string? process,
        HttpContext context)
    {
        var releases = await store.ListReleasesAsync(NullIfBlank(process), context.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new GpProcessReleaseListResponse
        {
            Releases = releases.Select(r => ToResponse(r, created: null)).ToArray(),
        });
    }

    private static async Task<IResult> HandleGetRelease(
        [FromServices] IGpProcessReleaseStore store,
        string digest,
        HttpContext context)
    {
        var record = await store.GetReleaseAsync(digest, context.RequestAborted).ConfigureAwait(false);
        return record is null
            ? Results.Problem(title: "No published release has that digest.", statusCode: StatusCodes.Status404NotFound)
            : Results.Content(record.Lock.ToIndentedJson(), "application/json", Encoding.UTF8);
    }

    private static async Task<IResult> HandleListBindings(
        [FromServices] IGpProcessReleaseStore store,
        [FromServices] GpProcessReleaseEnvironmentResolver environmentResolver,
        HttpContext context)
    {
        var environment = await environmentResolver.ResolveAsync(context.RequestAborted).ConfigureAwait(false);
        var bindings = await store.ListBindingsAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new GpProcessBindingListResponse
        {
            Bindings = bindings.Select(b => ToResponse(b.Process, b, environment)).ToArray(),
        });
    }

    private static async Task<IResult> HandleGetBinding(
        [FromServices] IGpProcessReleaseStore store,
        [FromServices] GpProcessReleaseEnvironmentResolver environmentResolver,
        string process,
        HttpContext context)
    {
        var environment = await environmentResolver.ResolveAsync(context.RequestAborted).ConfigureAwait(false);
        var binding = await store.GetBindingAsync(process, context.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ToResponse(process, binding, environment));
    }

    private static async Task<IResult> HandleActivateBinding(
        [FromServices] IGpProcessReleaseStore store,
        [FromServices] GpProcessReleaseEnvironmentResolver environmentResolver,
        string process,
        [FromBody] GpProcessBindingActivateRequest request,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.LockDigest))
        {
            return Results.Problem(title: "lockDigest is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Targets is null || request.Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Kind)))
        {
            return Results.Problem(title: "targets must contain valid provider identities.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.ExpectedBindingVersion < 0)
        {
            return Results.Problem(title: "expectedBindingVersion must be zero or positive.", statusCode: StatusCodes.Status400BadRequest);
        }

        var environment = await environmentResolver.ResolveAsync(context.RequestAborted).ConfigureAwait(false);
        var activation = new GpProcessBindingActivation
        {
            Process = process,
            LockDigest = request.LockDigest.Trim(),
            ExpectedBindingVersion = request.ExpectedBindingVersion,
            Operation = NullIfBlank(request.Operation) ?? "deploy",
            Actor = ActorOf(context),
            Targets = request.Targets
                .Select(t => new GpProcessBindingTargetIdentity { Kind = t.Kind, JobDefinitionArn = NullIfBlank(t.JobDefinitionArn) })
                .ToArray(),
        };

        var decision = await store.ActivateAsync(activation, environment, context.RequestAborted).ConfigureAwait(false);
        if (decision.Activated)
        {
            return Results.Ok(ToResponse(process, decision.Binding, environment));
        }

        return Results.Json(
            new GpProcessBindingRefusedResponse
            {
                Reasons = decision.Reasons,
                Details = decision.Details,
                Binding = ToResponse(process, decision.Binding, environment),
            },
            GpProcessReleaseJsonContext.Default.GpProcessBindingRefusedResponse,
            statusCode: StatusCodes.Status409Conflict);
    }

    private static GpProcessReleaseResponse ToResponse(GpProcessReleaseRecord record, bool? created)
        => new()
        {
            LockDigest = record.Digest,
            Process = record.Lock.Process.Name,
            Version = record.Lock.Process.Version,
            SourceSha = record.Lock.Source.Sha,
            PublishedBy = record.PublishedBy,
            PublishedAt = record.PublishedAt,
            Created = created,
        };

    private static GpProcessBindingResponse ToResponse(string process, GpProcessBinding? binding, GpProcessReleaseEnvironment environment)
        => new()
        {
            Process = process,
            BindingVersion = binding?.BindingVersion ?? 0,
            LockDigest = binding?.LockDigest,
            PreviousLockDigest = binding?.PreviousLockDigest,
            Targets = (binding?.Targets ?? [])
                .Select(t => new GpProcessBindingTargetResponse { Kind = t.Kind, Image = t.Image, JobDefinitionArn = t.JobDefinitionArn })
                .ToArray(),
            Operation = binding?.Operation,
            ActivatedBy = binding?.ActivatedBy,
            ActivatedAt = binding?.ActivatedAt,
            ServerContractVersion = environment.ServerContractVersion,
            SupportedTargets = environment.SupportedTargetKinds,
            ScheduleReconciliation = ScheduleReconciliationNote,
        };

    private static string ActorOf(HttpContext context)
        => NullIfBlank(context.User?.Identity?.Name) ?? "admin";

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
