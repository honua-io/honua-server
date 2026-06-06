// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.FormGeneration;
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

        // NL -> form generation. Reuses the workflow-generation provider config + chat plumbing
        // (registered by AddWorkflowGeneration) and constructs a generation-only FormPackageValidator
        // (stub target resolver) so generation never requires a live metadata database.
        services.TryAddSingleton<IFormGenerationService, FormGenerationService>();

        return services;
    }
}
