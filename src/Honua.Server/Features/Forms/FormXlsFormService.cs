// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Sdk.Field.Forms.Authoring;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Forms;

// Imports XLSForm workbooks as form package drafts and exports published packages as XLSForm.
internal sealed class FormXlsFormService
{
    internal const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly (int Status, string Detail) ImportProblem =
        (StatusCodes.Status400BadRequest, "The workbook is not an XLSForm survey a Honua form package can represent.");

    private static readonly (int Status, string Detail) ExportProblem =
        (StatusCodes.Status422UnprocessableEntity, "The published package uses behavior XLSForm cannot represent.");

    private readonly IFormPackageStore _store;
    private readonly IFormTargetMetadataResolver _targetResolver;
    private readonly FormXlsFormEvents _events;

    public FormXlsFormService(IFormPackageStore store, IFormTargetMetadataResolver targetResolver, FormXlsFormEvents events)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public async Task<IResult> ImportAsync(HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.ImportXlsForm");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "import-xlsform");

        var serviceId = context.Request.Query["serviceId"].ToString();
        if (string.IsNullOrWhiteSpace(serviceId)
            || !int.TryParse(context.Request.Query["layerId"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "The serviceId and integer layerId query parameters are required.");
        }

        var target = await ResolveTargetAsync(new FormTargetDefinition { ServiceId = serviceId, LayerId = layerId }, context.RequestAborted)
            .ConfigureAwait(false);
        if (target is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, $"Layer {layerId} of service '{serviceId}' was not found or is not routable.");
        }

        var upload = await FormXlsFormUpload.ReadAsync(context).ConfigureAwait(false);
        return upload.Error ?? await ImportWorkbookAsync(context, upload.Content, target).ConfigureAwait(false);
    }

    public async Task<IResult> ExportAsync(HttpContext context, string formId)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.ExportXlsForm");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "export-xlsform");

        var version = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, context.RequestAborted).ConfigureAwait(false);
        if (version is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' was not found.");
        }

        var mapped = FormXlsFormExportMapper.ToDefinition(version);
        var exported = mapped.Value is null ? null : XlsFormConverter.Export(mapped.Value);
        if (exported?.Value is null)
        {
            var diagnostics = exported?.Diagnostics ?? mapped.Diagnostics;
            _events.ExportRejected(version, diagnostics.Count);
            return DiagnosticsProblem(context, ExportProblem, diagnostics);
        }

        using var buffer = new MemoryStream();
        XlsFormWorkbookIO.Write(buffer, exported.Value);
        _events.Exported(version);
        context.Response.Headers.CacheControl = "no-store";
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{formId}-v{version.Version}.xlsx");
        return Results.File(buffer.ToArray(), WorkbookContentType, fileName);
    }

    private async Task<IResult> ImportWorkbookAsync(HttpContext context, byte[] content, FormXlsFormTarget target)
    {
        using var stream = new MemoryStream(content, writable: false);
        var workbook = XlsFormWorkbookIO.Read(stream);
        var imported = workbook.Value is null ? null : XlsFormConverter.Import(workbook.Value);
        var mapped = imported?.Value is null ? null : FormXlsFormImportMapper.ToPackage(imported.Value, workbook.Value!, target);
        if (mapped?.Value is null)
        {
            var diagnostics = mapped?.Diagnostics ?? imported?.Diagnostics ?? workbook.Diagnostics;
            _events.ImportRejected(diagnostics.Count);
            return DiagnosticsProblem(context, ImportProblem, diagnostics);
        }

        var actor = ActorResolver.Resolve(context, ActorResolution.SubjectOrName);
        var version = await _store.SaveDraftAsync(mapped.Value, actor, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.ETag = version.ETag;
        context.Response.Headers.CacheControl = "no-store";
        await _events.ImportedAsync(context, version).ConfigureAwait(false);
        return Results.Json(version, FormPackageJsonContext.Default.FormPackageVersion, statusCode: StatusCodes.Status201Created);
    }

    private async Task<FormXlsFormTarget?> ResolveTargetAsync(FormTargetDefinition definition, CancellationToken cancellationToken)
    {
        var resolution = await _targetResolver.ResolveAsync(definition, cancellationToken).ConfigureAwait(false);
        if (resolution.Resource is not { } resource)
        {
            return null;
        }

        return new FormXlsFormTarget
        {
            Definition = definition,
            Attributes = resource.SchemaFields
                .GroupBy(static field => field.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First().Type, StringComparer.OrdinalIgnoreCase),
            HasGeometry = (resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None) != MetadataV2GeometryType.None
        };
    }

    private static IResult DiagnosticsProblem(
        HttpContext context,
        (int Status, string Detail) outcome,
        IReadOnlyList<XlsFormDiagnostic> diagnostics)
    {
        var problem = ProblemDetailsHelpers.CreateAdminProblemDetails(context, outcome.Status, outcome.Detail);
        var body = new FormXlsFormProblem
        {
            Type = problem.Type,
            Title = problem.Title,
            Status = problem.Status,
            Detail = problem.Detail,
            Instance = problem.Instance,
            CorrelationId = problem.CorrelationId,
            Diagnostics = diagnostics.Select(FormXlsFormDiagnostic.From).ToArray()
        };
        return Results.Json(body, FormXlsFormJsonContext.Default.FormXlsFormProblem, ProblemDetailsHelpers.ContentType, outcome.Status);
    }
}
