// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public enum PortalAccessLevel
{
    Private = 0,

    Organization = 1,

    Public = 2,
}

public static class PortalAccessLevelExtensions
{
    public static string ToWireName(this PortalAccessLevel level) => level switch
    {
        PortalAccessLevel.Public => "public",
        PortalAccessLevel.Organization => "org",
        _ => "private",
    };
}
