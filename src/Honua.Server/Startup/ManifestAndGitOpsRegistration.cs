// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Startup;

/// <summary>
/// Registers manifest approval workflow services (webhook dispatcher, expiry sweep, request
/// gate) and the GitOps drift-watch hosted service.
/// </summary>
internal static class ManifestAndGitOpsRegistration
{
    public static IServiceCollection AddHonuaManifestAndGitOps(this IServiceCollection services, IConfiguration configuration)
    {
        // Manifest approval workflow services
        services.Configure<ManifestApprovalOptions>(
            configuration.GetSection(ManifestApprovalOptions.SectionName));
        services.Configure<ManifestApprovalWebhookOptions>(
            configuration.GetSection(ManifestApprovalWebhookOptions.SectionName));
        services.AddSingleton<IValidateOptions<ManifestApprovalWebhookOptions>,
            ManifestApprovalWebhookOptionsValidator>();
        services.AddResilientHttpClient(
            "manifest-approval-webhook",
            "manifest-approval-webhook",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
        services.AddSingleton<ManifestApprovalWebhookDispatcher>(sp =>
            new ManifestApprovalWebhookDispatcher(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<ManifestApprovalWebhookOptions>>(),
                sp.GetRequiredService<ILogger<ManifestApprovalWebhookDispatcher>>()));
        services.AddHostedService(sp =>
            sp.GetRequiredService<ManifestApprovalWebhookDispatcher>());
        services.AddHostedService(sp =>
            new ManifestApprovalExpiryService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<ManifestApprovalOptions>>(),
                sp.GetService<ManifestApprovalWebhookDispatcher>(),
                sp.GetRequiredService<ILogger<ManifestApprovalExpiryService>>()));
        services.AddScoped<ManifestApprovalGate>();

        // GitOps drift-watch hosted service (#518)
        services.Configure<GitOpsWatchOptions>(
            configuration.GetSection(GitOpsWatchOptions.SectionName));
        services.AddHostedService(sp =>
            new GitOpsWatchService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<GitOpsWatchOptions>>(),
                sp.GetRequiredService<ILogger<GitOpsWatchService>>()));

        return services;
    }
}
