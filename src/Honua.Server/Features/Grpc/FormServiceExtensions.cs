// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Abstractions;
using Honua.Server.Features.Forms.Services;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// Service registration extensions for form services.
/// </summary>
public static class FormServiceExtensions
{
    /// <summary>
    /// Registers form-related services in the dependency injection container.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration provider.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddFormServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register core form services
        services.AddScoped<IFormDefinitionStore, PostgresFormDefinitionStore>();
        services.AddScoped<IFormValidationService, FormValidationService>();
        services.AddScoped<IFormCollaborationManager, FormCollaborationManager>();

        // Register gRPC service
        services.AddScoped<HonuaFormService>();

        // Configure form service options
        services.Configure<FormServiceOptions>(configuration.GetSection("FormService"));

        return services;
    }

    /// <summary>
    /// Maps form gRPC service endpoints.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>Endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapFormGrpcService(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<HonuaFormService>();
        return endpoints;
    }
}

/// <summary>
/// Configuration options for form services.
/// </summary>
public class FormServiceOptions
{
    public const string SectionName = "FormService";

    /// <summary>
    /// Maximum number of concurrent collaboration sessions.
    /// </summary>
    public int MaxCollaborationSessions { get; set; } = 1000;

    /// <summary>
    /// Session timeout for inactive collaboration sessions.
    /// </summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether to enable real-time collaboration features.
    /// </summary>
    public bool EnableRealTimeCollaboration { get; set; } = true;

    /// <summary>
    /// Maximum form size in bytes.
    /// </summary>
    public long MaxFormSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB

    /// <summary>
    /// Maximum number of controls per form.
    /// </summary>
    public int MaxControlsPerForm { get; set; } = 100;

    /// <summary>
    /// Whether to enable form caching.
    /// </summary>
    public bool EnableFormCaching { get; set; } = true;

    /// <summary>
    /// Form cache TTL.
    /// </summary>
    public TimeSpan FormCacheTtl { get; set; } = TimeSpan.FromMinutes(30);
}