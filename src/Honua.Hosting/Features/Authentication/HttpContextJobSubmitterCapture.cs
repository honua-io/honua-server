// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Captures the submitter of in-process background work from the active job scope or, on a
/// request thread, from the request principal and its effective tenant.
/// </summary>
internal sealed class HttpContextJobSubmitterCapture(
    IHttpContextAccessor httpContextAccessor,
    IOptions<RbacOptions> rbacOptions) : IJobSubmitterCapture
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly IOptions<RbacOptions> _rbacOptions = rbacOptions ?? throw new ArgumentNullException(nameof(rbacOptions));

    /// <inheritdoc />
    public JobSecurityContext? CaptureCurrent()
    {
        if (JobSecurityScope.Current?.Submitter is { } submitter)
        {
            return submitter;
        }

        if (_httpContextAccessor.HttpContext is not { User: { } user } httpContext)
        {
            return null;
        }

        var tenantContext = httpContext.RequestServices?.GetService<ITenantContext>();
        return JobSecurityContextCapture.Capture(user, _rbacOptions.Value, tenantContext);
    }
}
