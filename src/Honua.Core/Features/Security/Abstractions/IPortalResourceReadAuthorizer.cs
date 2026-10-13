// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>Resolves resource-aware Portal reads through canonical authorization.</summary>
public interface IPortalResourceReadAuthorizer
{
    /// <summary>Evaluates a published resource or a layerless service read.</summary>
    /// <param name="principal">The actual caller.</param>
    /// <param name="resource">The published resource, when present.</param>
    /// <param name="service">The owning service.</param>
    /// <param name="publication">The exact publication, when discovery resolves one.</param>
    /// <returns>The canonical read decision.</returns>
    DomainAccessDecision Evaluate(ClaimsPrincipal principal, MetadataV2Resource? resource, MetadataV2Service service, MetadataV2Publication? publication = null);

    /// <summary>Whether live group membership grants an alternative service read audience.</summary>
    /// <remarks>Callers must enforce tenant, credential scope, and resource policy separately.</remarks>
    /// <param name="principal">The actual caller.</param>
    /// <param name="service">The exact shared service.</param>
    /// <returns>Whether the caller belongs to a group this service is shared with.</returns>
    bool HasServiceAudience(ClaimsPrincipal principal, MetadataV2Service service);
}
