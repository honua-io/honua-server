// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Infrastructure.Authentication;

// Server-derived semantic input to the existing Admin policy, never a fabricated
// request context. The target is the actual configured deployment authority.
internal sealed record SemanticImportAuthorizationResource(string TargetId, bool RequireWrite,
    OperatorAuthorizationRequest? SealedOperation = null)
{
    internal const string CanonicalPath = "/api/v1/admin/import/upload";
    internal string Method => RequireWrite ? HttpMethods.Post : HttpMethods.Get;

    internal OperatorAuthorizationRequest OperatorRequest => SealedOperation is { } exact
        ? exact with { ApprovalReasonCodes = exact.ApprovalReasonCodes.ToArray() }
        : new()
        {
            ResourceType = OperatorResourceType.Catalog,
            ResourceId = TargetId,
            Operation = RequireWrite ? OperatorOperation.Create : OperatorOperation.Read
        };

    internal bool IsValid => !string.IsNullOrWhiteSpace(TargetId) && TargetId.Length <= 512
        && (SealedOperation is null || SealedOperation.ResourceType == OperatorResourceType.Catalog
            && SealedOperation.ResourceId == TargetId
            && SealedOperation.Operation == (RequireWrite ? OperatorOperation.Create : OperatorOperation.Read)
            && SealedOperation.WorkspaceVisibility is null && SealedOperation.WorkspaceOwnerId is null
            && SealedOperation.WorkspaceScopeId is null && SealedOperation.ApprovalReasonCodes is not null
            && SealedOperation.ApprovalReasonCodes.Count <= 32);
}
