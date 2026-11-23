// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Import.FileImport;

internal static partial class ImportEndpoints
{
    private static Progress<UploadProgress>? CreateUploadProgressReporter(HttpContext context, string uploadId)
    {
        var progressStore = context.RequestServices.GetService<IUploadProgressStore>();
        if (progressStore == null)
        {
            return null;
        }

        var progressLogger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
        var tenantId = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        return new Progress<UploadProgress>(progress => _ = ReportProgressAsync(progress));

        async Task ReportProgressAsync(UploadProgress progress)
        {
            try
            {
                await progressStore.SetProgressAsync(uploadId, progress with { TenantId = tenantId }, TimeSpan.FromHours(1), CancellationToken.None);
            }
            // Intentionally generic: this is a best-effort progress update callback; a
            // failure to publish progress must not fail the underlying upload/import.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.ProgressUpdateFailed(progressLogger, uploadId, ex);
            }
        }
    }

    private static async Task<UploadProgress[]> VisibleUploadsAsync(
        HttpContext context,
        IReadOnlyList<UploadProgress> uploads,
        CancellationToken cancellationToken)
    {
        var visible = new List<UploadProgress>();
        foreach (var upload in uploads)
        {
            if (await OperationTenantAuthorization.CanAccessOperationAsync(context, upload, cancellationToken))
            {
                visible.Add(upload);
            }
        }

        return visible.ToArray();
    }
}
