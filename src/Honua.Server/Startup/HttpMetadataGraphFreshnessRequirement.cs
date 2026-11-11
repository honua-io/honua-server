// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Caching;

namespace Honua.Server.Startup;

// A read request outside the admin API may use a recently checked snapshot. Every other request,
// and any scope with no request at all, checks the current revision first.
internal sealed class HttpMetadataGraphFreshnessRequirement(IHttpContextAccessor httpContextAccessor)
    : IMetadataGraphFreshnessRequirement
{
    private const string AdminPathSegment = "/admin/";

    public bool RequireCurrentRevision
        => httpContextAccessor.HttpContext?.Request is not { } request
            || !IsReadMethod(request.Method)
            || IsAdminPath(request.Path);

    private static bool IsReadMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsAdminPath(PathString path)
        => path.HasValue
            && (path.Value.Contains(AdminPathSegment, StringComparison.OrdinalIgnoreCase)
                || path.Value.EndsWith("/admin", StringComparison.OrdinalIgnoreCase));
}
