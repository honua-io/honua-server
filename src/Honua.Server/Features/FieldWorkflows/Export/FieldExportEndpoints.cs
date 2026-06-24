// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.FieldWorkflows.Export;

/// <summary>
/// Admin REST surface for back-office field export packages (#1160). Every route
/// requires admin authorization and adapts to the shared
/// <see cref="FieldExportService"/>; every export is audited and respects the same
/// tenancy/RBAC posture as the review surface.
/// </summary>
internal static class FieldExportEndpoints
{
    public static IEndpointRouteBuilder MapFieldExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IFieldExportStore)))
        {
            return endpoints;
        }

        var admin = endpoints.MapGroup("/api/v{version:apiVersion}/admin/field-workflows/exports")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "FieldWorkflows")
            .RequireAdminAuthorization();

        admin.MapPost("", HandleCreate)
            .WithName("CreateFieldExport")
            .WithSummary("Generate a field export package over selected projects/forms/date-ranges/review-states (GeoJSON or CSV).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces(StatusCodes.Status200OK, contentType: "application/geo+json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        admin.MapGet("", HandleList)
            .WithName("ListFieldExports")
            .WithSummary("List previously generated field export packages (audit trail).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FieldExportRecordListResult>();

        return endpoints;
    }

    private static Task<IResult> HandleCreate(
        HttpContext context,
        [FromServices] FieldExportService service)
        => service.CreateAsync(context);

    private static Task<IResult> HandleList(
        HttpContext context,
        [FromServices] FieldExportService service)
        => service.ListAsync(context);
}
