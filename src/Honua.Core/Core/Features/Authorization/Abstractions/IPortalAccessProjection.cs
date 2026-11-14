// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// The per-principal visibility of a Portal item, the projection of an
/// authorization decision onto the facade's discovery/open semantics.
/// </summary>
/// <param name="IsVisible"><see langword="true"/> when the principal may see the
/// item in listings and open its metadata.</param>
/// <param name="RequiresAuthentication"><see langword="true"/> when the item is
/// hidden only because the caller is anonymous and authenticating could reveal
/// it; lets the facade emit a 401 challenge rather than silently omitting it.</param>
public readonly record struct PortalItemVisibility(bool IsVisible, bool RequiresAuthentication)
{
    /// <summary>Item is visible to the principal.</summary>
    public static PortalItemVisibility Visible => new(true, false);

    /// <summary>Item is hidden; the principal is forbidden.</summary>
    public static PortalItemVisibility Hidden => new(false, false);

    /// <summary>Item is hidden because authentication is required first.</summary>
    public static PortalItemVisibility AuthenticationRequired => new(false, true);
}
