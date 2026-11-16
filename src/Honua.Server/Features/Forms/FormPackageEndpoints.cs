// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Server.Features.Admin;
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

        var admin = endpoints.MapGroup("/api/v{version:apiVersion}/admin/forms")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Forms")
            .RequireAdminAuthorization();

        admin.MapGet("", HandleListForms)
            .WithName("ListForms")
            .WithSummary("List Forms.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageSummary[]>();

        admin.MapPost("", HandleCreateDraft)
            .WithName("CreateFormDraft")
            .WithSummary("Create a new Form Draft.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageVersion>(StatusCodes.Status201Created);

        admin.MapGet("/{formId}", HandleGetAdminCurrent)
            .WithName("GetCurrentForm")
            .WithSummary("Get the current Form Draft, falling back to the Released Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapGet("/{formId}/versions", HandleListVersions)
            .WithName("ListFormVersions")
            .WithSummary("List Form Versions.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion[]>();

        admin.MapGet("/{formId}/versions/{formVersion:int}", HandleGetVersion)
            .WithName("GetFormVersion")
            .WithSummary("Get a Form Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPut("/{formId}/versions/{formVersion:int}", HandleUpdateDraft)
            .WithName("UpdateFormDraft")
            .WithSummary("Update a Form Draft using If-Match optimistic concurrency.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Put]))
            .Produces<FormPackageVersion>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/{formId}/versions/{formVersion:int}/validate", HandleValidate)
            .WithName("ValidateFormDraft")
            .WithSummary("Validate a Form Version against target schema and policy.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageValidationResult>();

        admin.MapPost("/{formId}/versions/{formVersion:int}/publish", HandlePublish)
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .WithName("PublishFormDraft")
            .WithSummary("Validate a Form Draft and go live with an immutable Released Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageVersion>();

        admin.MapPost("/{formId}/versions/{formVersion:int}/reopen", HandleReopen)
            .WithName("ReopenFormVersion")
            .WithSummary("Create a new Form Draft from a Released Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormPackageVersion>();

        var runtime = endpoints.MapGroup("/api/v{version:apiVersion}/forms")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Forms")
            .RequireAdminAuthorization();

        runtime.MapGet("/{formId}", HandleGetRuntimeCurrent)
            .WithName("GetPublishedForm")
            .WithSummary("Get the current Released Form Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>();

        runtime.MapGet("/{formId}/versions/{formVersion:int}", HandleGetRuntimeVersion)
            .WithName("GetPublishedFormVersion")
            .WithSummary("Get a Released Form Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormPackageVersion>();

        runtime.MapGet("/{formId}/offline-policy", HandleGetOfflinePolicy)
            .WithName("GetFormOfflinePolicy")
            .WithSummary("Discover offline sync policy and sync endpoint links for a Released Form Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormOfflinePolicyResponse>();

        runtime.MapGet("/{formId}/compatibility", HandleGetCompatibility)
            .WithName("GetFormCompatibility")
            .WithSummary("Get the offline compatibility and migration manifest for a Released Form Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FormCompatibilityManifest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        runtime.MapPost("/{formId}/submissions", HandleSubmit)
            .WithName("SubmitForm")
            .WithSummary("Submit field data and optional attachments for a Released Form Version.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FormSubmissionResponse>();

        return endpoints;
    }

    private static Task<IResult> HandleListForms(
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
        int formVersion)
        => service.GetVersionAsync(context, formId, formVersion);

    private static Task<IResult> HandleUpdateDraft(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int formVersion)
        => service.UpdateDraftAsync(context, formId, formVersion);

    private static Task<IResult> HandleValidate(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int formVersion)
        => service.ValidateAsync(context, formId, formVersion);

    private static Task<IResult> HandlePublish(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int formVersion)
        => service.PublishAsync(context, formId, formVersion);

    private static Task<IResult> HandleReopen(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int formVersion)
        => service.ReopenAsync(context, formId, formVersion);

    private static Task<IResult> HandleGetRuntimeCurrent(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId)
        => service.GetRuntimeCurrentAsync(context, formId);

    private static Task<IResult> HandleGetRuntimeVersion(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        int formVersion)
        => service.GetRuntimeVersionAsync(context, formId, formVersion);

    private static Task<IResult> HandleGetCompatibility(
        HttpContext context,
        [FromServices] FormPackageLifecycleService service,
        string formId,
        [FromQuery] int? clientVersion)
    {
        var entitlementGate = LicenseGate.RequireEntitlement(
            context,
            FeatureCatalog.FieldOpsOfflineSyncKey,
            "Form offline compatibility");
        return entitlementGate is not null
            ? Task.FromResult(entitlementGate)
            : service.GetCompatibilityAsync(context, formId, clientVersion);
    }

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
