// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Infrastructure.Authentication;

// The initial contract is explicit. Catalog Analytic/Managed flags alone cannot
// establish bounded source authority for arbitrary processes or text selectors.
internal static class AgentManagedProcessAuthorization
{
    internal const string Operation = "managed-process";
    internal const string ProcessId = "analytics.buffer-aggregate-managed";
    internal const string Permission = "agent:" + Operation + ":" + ProcessId;
    internal const string ResourceId = Operation + ":" + ProcessId;

    internal static bool AllowsOperator(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => AgentCredentialAuthority.IsAgent(principal) && principal.HasClaim("permission", Permission)
            && request.ResourceType == OperatorResourceType.Process && request.Operation == OperatorOperation.Execute
            && request.ResourceId == ResourceId;
}
