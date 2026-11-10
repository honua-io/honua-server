// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.VersionManagementServer;

internal static class VersionAccessPolicy
{
    internal static bool CanCreateVersion(
        VersionAccess access,
        string? callerName,
        bool isAuthenticated)
        => access != VersionAccess.Private
           || (isAuthenticated && !string.IsNullOrWhiteSpace(callerName));

    // Private versions are visible only to their owner and service administrators; public and protected versions are visible to any reader, and protected edits stay owner-only.
    internal static bool IsVersionVisible(GdbVersion version, string? callerName, bool isAdmin)
        => isAdmin
           || version.Access != VersionAccess.Private
           || string.Equals(version.Owner, callerName, StringComparison.OrdinalIgnoreCase);

    internal static bool CanManageVersion(GdbVersion version, string? callerName, bool isAdmin)
        => isAdmin
           || string.Equals(version.Owner, callerName, StringComparison.OrdinalIgnoreCase);
}
