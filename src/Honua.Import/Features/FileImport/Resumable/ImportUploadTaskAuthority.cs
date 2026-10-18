// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Resolves the actual caller through strict task ownership or the explicit human
/// contribution authority. A contribution never changes task or upload ownership.
/// </summary>
internal sealed class ImportUploadTaskAuthority(IDatasetImportTaskProvider tasks,
    IDatasetImportContributorAuthorizer? contributors = null)
{
    internal async Task<ImportUploadAuthorization> AuthorizeAsync(ClaimsPrincipal principal,
        ImportUploadTaskReference reference, bool requireWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(reference);
        var task = await tasks.AuthorizeAsync(principal, reference.WorkspaceId, reference.TaskId, reference.Revision,
            reference.ContentHash, requireWrite, cancellationToken).ConfigureAwait(false);
        var actor = task?.OwnerActorId;
        if (task is null && contributors is not null)
        {
            var contribution = await contributors.AuthorizeAsync(principal, reference.WorkspaceId, reference.TaskId,
                reference.Revision, reference.ContentHash, requireWrite, cancellationToken).ConfigureAwait(false);
            if (contribution is not null && contribution.ExecutorActorId == contribution.Task.OwnerActorId &&
                contribution.ContributorActorId == contribution.InitiatingHumanActorId &&
                contribution.ContributorActorId != contribution.ExecutorActorId)
            {
                task = contribution.Task;
                actor = contribution.ContributorActorId;
            }
        }
        if (task is null || task.TaskId != reference.TaskId || task.WorkspaceId != reference.WorkspaceId ||
            task.Revision != reference.Revision || task.ContentHash != reference.ContentHash ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 512 || actor.Any(char.IsControl))
        {
            throw new UnauthorizedAccessException("Current access to the original import task is required.");
        }
        return new(task, actor);
    }
}

internal sealed record ImportUploadAuthorization(DatasetImportTask Task, string ContributorActorId);
