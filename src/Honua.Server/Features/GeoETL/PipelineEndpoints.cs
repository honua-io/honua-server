// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services;
using Honua.Server.Features.GeoETL.Models;
using Honua.Server.Features.Infrastructure;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.GeoETL;

/// <summary>
/// Minimal API surface for GeoETL pipeline authoring and execution. CRUD is
/// edition-agnostic (operators may stage definitions ahead of an upgrade); the run and
/// dry-run endpoints submit an <c>ExtractTransformLoad</c> job through the durable
/// substrate (#681). See the GeoETL roadmap § Child Ticket A and ADR-0038.
/// </summary>
internal static class PipelineEndpoints
{
    /// <summary>
    /// Maps the GeoETL pipeline endpoints under <c>/api/v{version}/admin/geoetl/pipelines</c>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    public static void MapGeoEtlPipelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/geoetl/pipelines")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "GeoETL")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListPipelines)
            .WithName("ListGeoEtlPipelines")
            .WithSummary("List GeoETL pipeline definitions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreatePipeline)
            .WithName("CreateGeoEtlPipeline")
            .WithSummary("Create a GeoETL pipeline definition")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id}", HandleGetPipeline)
            .WithName("GetGeoEtlPipeline")
            .WithSummary("Get a GeoETL pipeline definition")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id}", HandleUpdatePipeline)
            .WithName("UpdateGeoEtlPipeline")
            .WithSummary("Replace a GeoETL pipeline definition")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{id}", HandleDeletePipeline)
            .WithName("DeleteGeoEtlPipeline")
            .WithSummary("Delete a GeoETL pipeline definition")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapPost("/{id}/run", HandleRunPipeline)
            .WithName("RunGeoEtlPipeline")
            .WithSummary("Submit a GeoETL pipeline run through the durable substrate")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{id}/dry-run", HandleDryRunPipeline)
            .WithName("DryRunGeoEtlPipeline")
            .WithSummary("Submit a GeoETL pipeline dry run (null preview sink)")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id}/executions", HandleListExecutions)
            .WithName("ListGeoEtlPipelineExecutions")
            .WithSummary("List executions for a GeoETL pipeline")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{id}/executions/{executionId}", HandleGetExecution)
            .WithName("GetGeoEtlPipelineExecution")
            .WithSummary("Get a single GeoETL pipeline execution")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<IResult> HandleListPipelines(
        [FromServices] IPipelineDefinitionStore store,
        CancellationToken cancellationToken)
    {
        var definitions = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var payload = new PipelineListResponse
        {
            Pipelines = definitions.Select(ToResponse).ToList()
        };
        return Results.Json(
            ApiResponse<PipelineListResponse>.CreateSuccess(payload),
            PipelineJsonContext.Default.ApiResponsePipelineListResponse);
    }

    private static async Task<IResult> HandleCreatePipeline(
        HttpContext context,
        [FromServices] IPipelineDefinitionStore store,
        [FromServices] PipelineValidator validator,
        CancellationToken cancellationToken)
    {
        var request = await ReadRequestAsync(context).ConfigureAwait(false);
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest, "Invalid or missing request body.");
        }

        var id = $"pl-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var definition = ToDefinition(request, id, version: 1, now, now);

        var validation = validator.Validate(definition);
        if (!validation.IsValid)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                string.Join("; ", validation.Errors.Select(e => $"{e.Code}: {e.Message}")));
        }

        await store.CreateAsync(definition, cancellationToken).ConfigureAwait(false);

        var response = ToResponse(definition) with
        {
            Advisories = validation.Advisories.Select(a => a.Message).ToList()
        };
        return Results.Json(
            ApiResponse<PipelineDefinitionResponse>.CreateSuccess(response),
            PipelineJsonContext.Default.ApiResponsePipelineDefinitionResponse,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> HandleGetPipeline(
        string id,
        HttpContext context,
        [FromServices] IPipelineDefinitionStore store,
        CancellationToken cancellationToken)
    {
        var definition = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Pipeline '{id}' not found.");
        }

        return Results.Json(
            ApiResponse<PipelineDefinitionResponse>.CreateSuccess(ToResponse(definition)),
            PipelineJsonContext.Default.ApiResponsePipelineDefinitionResponse);
    }

    private static async Task<IResult> HandleUpdatePipeline(
        string id,
        HttpContext context,
        [FromServices] IPipelineDefinitionStore store,
        [FromServices] PipelineValidator validator,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Pipeline '{id}' not found.");
        }

        var request = await ReadRequestAsync(context).ConfigureAwait(false);
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest, "Invalid or missing request body.");
        }

        var candidate = ToDefinition(request, id, existing.Version, existing.CreatedAt, DateTimeOffset.UtcNow);
        var validation = validator.Validate(candidate);
        if (!validation.IsValid)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                string.Join("; ", validation.Errors.Select(e => $"{e.Code}: {e.Message}")));
        }

        var updated = await store.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Pipeline '{id}' not found.");
        }

        var response = ToResponse(updated) with
        {
            Advisories = validation.Advisories.Select(a => a.Message).ToList()
        };
        return Results.Json(
            ApiResponse<PipelineDefinitionResponse>.CreateSuccess(response),
            PipelineJsonContext.Default.ApiResponsePipelineDefinitionResponse);
    }

    private static async Task<IResult> HandleDeletePipeline(
        string id,
        HttpContext context,
        [FromServices] IPipelineDefinitionStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return deleted
            ? Results.NoContent()
            : ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Pipeline '{id}' not found.");
    }

    private static Task<IResult> HandleRunPipeline(
        string id,
        HttpContext context,
        [FromServices] IPipelineDefinitionStore store,
        [FromServices] PipelineExecutionLauncher launcher,
        CancellationToken cancellationToken)
        => SubmitAsync(id, context, store, launcher, dryRun: false, cancellationToken);

    private static Task<IResult> HandleDryRunPipeline(
        string id,
        HttpContext context,
        [FromServices] IPipelineDefinitionStore store,
        [FromServices] PipelineExecutionLauncher launcher,
        CancellationToken cancellationToken)
        => SubmitAsync(id, context, store, launcher, dryRun: true, cancellationToken);

    private static async Task<IResult> SubmitAsync(
        string id,
        HttpContext context,
        IPipelineDefinitionStore store,
        PipelineExecutionLauncher launcher,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var definition = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Pipeline '{id}' not found.");
        }

        var execution = await launcher
            .LaunchAsync(definition, dryRun, context.User.Identity?.Name, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            ApiResponse<PipelineExecutionResponse>.CreateSuccess(ToResponse(execution)),
            PipelineJsonContext.Default.ApiResponsePipelineExecutionResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> HandleListExecutions(
        string id,
        [FromServices] IPipelineExecutionStore store,
        CancellationToken cancellationToken)
    {
        var executions = await store.ListForPipelineAsync(id, cancellationToken).ConfigureAwait(false);
        var payload = new PipelineExecutionListResponse
        {
            Executions = executions.Select(ToResponse).ToList()
        };
        return Results.Json(
            ApiResponse<PipelineExecutionListResponse>.CreateSuccess(payload),
            PipelineJsonContext.Default.ApiResponsePipelineExecutionListResponse);
    }

    private static async Task<IResult> HandleGetExecution(
        string id,
        string executionId,
        HttpContext context,
        [FromServices] IPipelineExecutionStore store,
        CancellationToken cancellationToken)
    {
        var execution = await store.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null || !string.Equals(execution.PipelineId, id, StringComparison.Ordinal))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Execution '{executionId}' not found for pipeline '{id}'.");
        }

        return Results.Json(
            ApiResponse<PipelineExecutionResponse>.CreateSuccess(ToResponse(execution)),
            PipelineJsonContext.Default.ApiResponsePipelineExecutionResponse);
    }

    private static async Task<PipelineDefinitionRequest?> ReadRequestAsync(HttpContext context)
    {
        try
        {
            return await context.Request
                .ReadFromJsonAsync(PipelineJsonContext.Default.PipelineDefinitionRequest, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PipelineDefinition ToDefinition(
        PipelineDefinitionRequest request,
        string id,
        int version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        => new()
        {
            SchemaVersion = request.SchemaVersion,
            Id = id,
            Name = request.Name,
            Description = request.Description,
            Version = version,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Stages = request.Stages.Select(ToStage).ToArray()
        };

    private static PipelineStage ToStage(PipelineStageDto dto)
    {
        var kind = dto.Kind.ToLowerInvariant() switch
        {
            "source" => PipelineStageKind.Source,
            "transform" => PipelineStageKind.Transform,
            "sink" => PipelineStageKind.Sink,
            _ => throw new InvalidOperationException($"Unknown stage kind '{dto.Kind}'.")
        };

        return new PipelineStage
        {
            Kind = kind,
            Connector = dto.Connector is null
                ? null
                : new ConnectorConfig { Type = dto.Connector.Type, Options = dto.Connector.Options },
            Transform = dto.Transform is null
                ? null
                : new TransformConfig { Type = dto.Transform.Type, Options = dto.Transform.Options }
        };
    }

    private static PipelineDefinitionResponse ToResponse(PipelineDefinition definition)
        => new()
        {
            SchemaVersion = definition.SchemaVersion,
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            Version = definition.Version,
            CreatedAt = definition.CreatedAt,
            UpdatedAt = definition.UpdatedAt,
            Stages = definition.Stages.Select(ToStageDto).ToList()
        };

    private static PipelineStageDto ToStageDto(PipelineStage stage)
        => new()
        {
            Kind = stage.Kind.ToString().ToLowerInvariant(),
            Connector = stage.Connector is null
                ? null
                : new ConnectorConfigDto
                {
                    Type = stage.Connector.Type,
                    Options = new Dictionary<string, string>(stage.Connector.Options, StringComparer.Ordinal)
                },
            Transform = stage.Transform is null
                ? null
                : new TransformConfigDto
                {
                    Type = stage.Transform.Type,
                    Options = new Dictionary<string, string>(stage.Transform.Options, StringComparer.Ordinal)
                }
        };

    private static PipelineExecutionResponse ToResponse(PipelineExecution execution)
        => new()
        {
            Id = execution.Id,
            PipelineId = execution.PipelineId,
            PipelineVersion = execution.PipelineVersion,
            ExecutionJobId = execution.ExecutionJobId,
            Status = execution.Status.ToString(),
            IsDryRun = execution.IsDryRun,
            FeaturesRead = execution.FeaturesRead,
            FeaturesWritten = execution.FeaturesWritten,
            FeaturesQuarantined = execution.FeaturesQuarantined,
            BatchId = execution.BatchId,
            ErrorMessage = execution.ErrorMessage,
            CreatedAt = execution.CreatedAt,
            CompletedAt = execution.CompletedAt
        };
}
