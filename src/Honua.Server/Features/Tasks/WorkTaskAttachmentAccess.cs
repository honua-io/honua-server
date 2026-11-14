// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Server.Features.Assets;
using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAttachmentAccess(IWorkTaskStore tasks, AssetFormLookup forms, IConfigurationGenerationSource generation)
{
    public Task<long> GetGenerationAsync(CancellationToken cancellationToken)
        => generation.GetGenerationAsync(cancellationToken);

    public async Task<WorkTask?> FindTaskAsync(HttpContext context, Guid taskId)
    {
        var task = await tasks.GetAsync(taskId, context.RequestAborted).ConfigureAwait(false);
        return task is not null && (await AssetServiceAccess.RequireEditorAsync(context, task.ServiceId).ConfigureAwait(false)).Denied is null
            ? task : null;
    }

    public Task<FormPackageVersion?> FindFormAsync(WorkTask task, CancellationToken cancellationToken)
        => forms.FindPinnedAsync(task, cancellationToken);
}
