// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.FormGeneration;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Forms;

internal static class FormPackageEndpoints
{
    public static IEndpointRouteBuilder MapFormPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IFormPackageStore)))
        {
            return endpoints;
        }

        var admin = endpoints.MapGroup("/api/v{version:apiVersion}/admin/forms/packages")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Forms")
            .RequireAdminAuthorization();

        admin.MapGet("", HandleListPackages)
            .WithName("ListFormPackages")
            .WithSummary("List server-owned form packages.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageSummary[]>();

        admin.MapPost("", HandleCreateDraft)
            .WithName("CreateFormPackageDraft")
            .WithSummary("Create a new draft form package version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageVersion>(StatusCodes.Status201Created);

        admin.MapGet("/{formId}", HandleGetAdminCurrent)
            .WithName("GetCurrentFormPackage")
            .WithSummary("Get the current draft package, falling back to the current published version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapGet("/{formId}/versions", HandleListVersions)
            .WithName("ListFormPackageVersions")
            .WithSummary("List package versions.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion[]>();

        admin.MapGet("/{formId}/versions/{packageVersion:int}", HandleGetVersion)
            .WithName("GetFormPackageVersion")
            .WithSummary("Get a package version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPut("/{formId}/versions/{packageVersion:int}", HandleUpdateDraft)
            .WithName("UpdateFormPackageDraft")
            .WithSummary("Update a draft package version using If-Match optimistic concurrency.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Put]))
            .Produces<FormPackageVersion>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/{formId}/versions/{packageVersion:int}/validate", HandleValidate)
            .WithName("ValidateFormPackageDraft")
            .WithSummary("Validate a package version against target schema and policy.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageValidationResult>();

        admin.MapPost("/{formId}/versions/{packageVersion:int}/publish", HandlePublish)
            .WithName("PublishFormPackageDraft")
            .WithSummary("Validate and publish an immutable package version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageVersion>();

        admin.MapPost("/{formId}/versions/{packageVersion:int}/reopen", HandleReopen)
            .WithName("ReopenFormPackageVersion")
            .WithSummary("Create a new draft from a published package version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageVersion>();

        admin.MapPost("/generate", HandleGenerateForm)
            .WithName("GenerateFormPackage")
            .WithSummary("Generate or refine a form package from a natural-language prompt.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<GenerateFormPackageRequest>("application/json")
            .Produces<FormGenerationResult>();

        var runtime = endpoints.MapGroup("/api/v{version:apiVersion}/forms/packages")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Forms")
            .RequireAdminAuthorization();

        runtime.MapGet("/{formId}", HandleGetRuntimeCurrent)
            .WithName("GetPublishedFormPackage")
            .WithSummary("Get the current published form package.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>();

        runtime.MapGet("/{formId}/versions/{packageVersion:int}", HandleGetRuntimeVersion)
            .WithName("GetPublishedFormPackageVersion")
            .WithSummary("Get a published form package version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>();

        runtime.MapGet("/{formId}/offline-policy", HandleGetOfflinePolicy)
            .WithName("GetFormOfflinePolicy")
            .WithSummary("Discover offline sync policy and existing sync endpoint links for a published package.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormOfflinePolicyResponse>();

        runtime.MapGet("/{formId}/compatibility", HandleGetCompatibility)
            .WithName("GetFormPackageCompatibility")
            .WithSummary("Get the offline compatibility and migration manifest for a published package version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormCompatibilityManifest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        runtime.MapPost("/{formId}/submissions", HandleSubmit)
            .WithName("SubmitFormPackage")
            .WithSummary("Submit field data and optional attachments for a published package.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormSubmissionResponse>();

        return endpoints;
    }

    private static async Task<IResult> HandleGenerateForm(
        HttpContext context,
        [FromServices] IFormGenerationService generation)
    {
        GenerateFormPackageRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                FormGenerationApiJsonContext.Default.GenerateFormPackageRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            var bad = new FormGenerationResult { Status = "error", Rationale = "A non-empty 'prompt' is required." };
            return Results.Json(bad, FormGenerationApiJsonContext.Default.FormGenerationResult, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await generation.GenerateAsync(
            new FormGenerationRequest
            {
                Prompt = request.Prompt,
                Provider = request.Provider,
                Model = request.Model,
                CurrentForm = request.Package,
                Conversation = request.Conversation,
                Answers = request.Answers
            },
            context.RequestAborted).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(result, FormGenerationApiJsonContext.Default.FormGenerationResult);
    }

    private static Task<IResult> HandleListPackages(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service)
        => service.ListAsync(context);

    private static Task<IResult> HandleCreateDraft(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service)
        => service.CreateDraftAsync(context);

    private static Task<IResult> HandleGetAdminCurrent(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId)
        => service.GetAdminCurrentAsync(context, formId);

    private static Task<IResult> HandleListVersions(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId)
        => service.ListVersionsAsync(context, formId);

    private static Task<IResult> HandleGetVersion(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int packageVersion)
        => service.GetVersionAsync(context, formId, packageVersion);

    private static Task<IResult> HandleUpdateDraft(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int packageVersion)
        => service.UpdateDraftAsync(context, formId, packageVersion);

    private static Task<IResult> HandleValidate(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int packageVersion)
        => service.ValidateAsync(context, formId, packageVersion);

    private static Task<IResult> HandlePublish(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int packageVersion)
        => service.PublishAsync(context, formId, packageVersion);

    private static Task<IResult> HandleReopen(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int packageVersion)
        => service.ReopenAsync(context, formId, packageVersion);

    private static Task<IResult> HandleGetRuntimeCurrent(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId)
        => service.GetRuntimeCurrentAsync(context, formId);

    private static Task<IResult> HandleGetRuntimeVersion(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int packageVersion)
        => service.GetRuntimeVersionAsync(context, formId, packageVersion);

    private static Task<IResult> HandleGetCompatibility(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        [FromQuery] int? clientVersion)
        => service.GetCompatibilityAsync(context, formId, clientVersion);

    private static Task<IResult> HandleGetOfflinePolicy(
        HttpContext context,
        [FromServices] FormOfflinePolicyService service,
        string formId)
        => service.GetAsync(context, formId);

    private static Task<IResult> HandleSubmit(
        HttpContext context,
        [FromServices] FormSubmissionService service,
        string formId)
        => service.SubmitAsync(context, formId);
}
