// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Server.Features.Forms;

// Audit and log outcomes of XLSForm import and export.
internal sealed class FormXlsFormEvents(IAuditLog auditLog, ILogger<FormXlsFormEvents> logger)
{
    public async Task ImportedAsync(HttpContext context, FormPackageVersion version)
    {
        await FormPackageAudit.RecordAsync(auditLog, context, new FormPackageAuditEntry
        {
            Action = "forms.package.import_xlsform",
            FormId = version.FormId,
            Outcome = AuditOutcome.Success,
            Details = $"{{\"version\":{version.Version}}}"
        }).ConfigureAwait(false);
        FormPackageLog.XlsFormImported(logger, version.FormId, version.Version);
    }

    public void ImportRejected(int diagnosticCount)
        => FormPackageLog.XlsFormImportRejected(logger, diagnosticCount);

    public void Exported(FormPackageVersion version)
        => FormPackageLog.XlsFormExported(logger, version.FormId, version.Version);

    public void ExportRejected(FormPackageVersion version, int diagnosticCount)
        => FormPackageLog.XlsFormExportRejected(logger, version.FormId, diagnosticCount);
}

internal static partial class FormPackageLog
{
    [LoggerMessage(EventId = 118405, Level = LogLevel.Information, Message = "Imported XLSForm workbook as form package draft {FormId} version {Version}.")]
    public static partial void XlsFormImported(ILogger logger, string formId, int version);

    [LoggerMessage(EventId = 118406, Level = LogLevel.Information, Message = "Rejected XLSForm workbook import with {DiagnosticCount} diagnostics.")]
    public static partial void XlsFormImportRejected(ILogger logger, int diagnosticCount);

    [LoggerMessage(EventId = 118407, Level = LogLevel.Information, Message = "Exported form package {FormId} version {Version} as XLSForm.")]
    public static partial void XlsFormExported(ILogger logger, string formId, int version);

    [LoggerMessage(EventId = 118408, Level = LogLevel.Information, Message = "Rejected XLSForm export of form package {FormId} with {DiagnosticCount} diagnostics.")]
    public static partial void XlsFormExportRejected(ILogger logger, string formId, int diagnosticCount);
}
