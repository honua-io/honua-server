// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal static class FolderOperationCredential
{
    internal static bool IsFolderMutation(string operationId)
        => operationId is "folder.create" or "folder.update" or "folder.delete" or "service.move";

    internal static string ProposalGrant(string proposalId)
        => $"{AdminApiKeyPermission.ApprovedOperationGrantPrefix}proposal:{proposalId}";
}
