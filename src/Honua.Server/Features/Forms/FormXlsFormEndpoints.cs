// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Asp.Versioning.Builder;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Forms;

internal static class FormXlsFormEndpoints
{
    public static IVersionedEndpointRouteBuilder MapFormXlsFormEndpoints(this IVersionedEndpointRouteBuilder admin)
    {
        admin.MapPost("/import", HandleImport)
            .WithName("ImportFormXlsForm")
            .WithSummary("Import an XLSForm workbook as a new Form Draft.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<FormPackageVersion>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .RequireAdminAuthorization()
            .DisableAntiforgery();

        admin.MapGet("/{formId}/export", HandleExport)
            .WithName("ExportFormXlsForm")
            .WithSummary("Export the current Released Form Version as an XLSForm workbook.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces(StatusCodes.Status200OK, contentType: FormXlsFormService.WorkbookContentType)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return admin;
    }

    private static Task<IResult> HandleImport(HttpContext context, [FromServices] FormXlsFormService service)
        => service.ImportAsync(context);

    private static Task<IResult> HandleExport(HttpContext context, [FromServices] FormXlsFormService service, string formId)
        => service.ExportAsync(context, formId);
}
