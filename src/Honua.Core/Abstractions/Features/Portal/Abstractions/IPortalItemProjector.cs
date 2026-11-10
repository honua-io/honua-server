// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Core.Features.Portal.Abstractions;

public interface IPortalItemProjector
{
    IReadOnlyList<PortalItem> ProjectVisibleItems(
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal principal,
        string baseUrl);

    PortalItem? ProjectItem(
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal principal,
        string itemId,
        string baseUrl);
}
