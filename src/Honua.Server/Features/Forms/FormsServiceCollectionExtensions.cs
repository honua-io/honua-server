// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Forms;

internal static class FormsServiceCollectionExtensions
{
    public static IServiceCollection AddForms(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddScoped<FormPackageValidator>();
        services.AddScoped<FormPackageLifecycleService>();
        services.AddScoped<FormOfflinePolicyService>();
        services.AddScoped<FormSubmissionService>();

        return services;
    }
}
