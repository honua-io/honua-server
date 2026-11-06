// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Features.Security.Abstractions;

public interface IPortalResourceReadAuthorizer
{
    DomainAccessDecision Evaluate(ClaimsPrincipal principal, MetadataV2Resource? resource, MetadataV2Service service, MetadataV2Publication? publication = null);

    // Group membership is only an alternate read audience; callers must still enforce tenant, credential scope, and resource policy.
    bool HasServiceAudience(ClaimsPrincipal principal, MetadataV2Service service);
}
