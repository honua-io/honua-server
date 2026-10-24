// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
        services.TryAddScoped<IFormTargetMetadataResolver, FormTargetMetadataResolver>();
        services.AddScoped<FormPackageLifecycleService>();
        services.AddScoped<FormOfflinePolicyService>();
        services.AddScoped<FormSubmissionService>();

        return services;
    }
}
