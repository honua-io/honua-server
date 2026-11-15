// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Abstractions;

// RequiresAuthentication marks an item hidden only because the caller is anonymous, so the
// Portal facade can answer with a 401 challenge instead of silently omitting it.
public readonly record struct PortalItemVisibility(bool IsVisible, bool RequiresAuthentication)
{
    public static PortalItemVisibility Visible => new(true, false);

    public static PortalItemVisibility Hidden => new(false, false);

    public static PortalItemVisibility AuthenticationRequired => new(false, true);
}
