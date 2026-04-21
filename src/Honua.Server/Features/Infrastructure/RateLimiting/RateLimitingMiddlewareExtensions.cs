// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.RateLimiting;

/// <summary>
/// Extension methods for configuring rate limiting middleware and services.
/// </summary>
public static class RateLimitingMiddlewareExtensions
{
    /// <summary>
    /// Adds rate limiting services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The application configuration</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure rate limiting options
        services.Configure<RateLimitingOptions>(
            configuration.GetSection(RateLimitingOptions.SectionName));

        // Register rate limit policy store
        services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();

        // Validate options at startup
        services.AddSingleton<IValidateOptions<RateLimitingOptions>>(
            new RateLimitingOptionsValidator());

        return services;
    }

    /// <summary>
    /// Adds the rate limiting middleware to the application pipeline.
    /// Should be registered after authentication but before authorization.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RateLimitingMiddleware>();
    }
}

/// <summary>
/// Validator for rate limiting options to ensure secure configuration.
/// </summary>
internal sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        var errors = new List<string>();

        // Validate global rate limits
        if (options.GlobalRequestsPerMinute <= 0)
        {
            errors.Add("GlobalRequestsPerMinute must be greater than 0");
        }

        if (options.GlobalRequestsPerMinute > 100000)
        {
            errors.Add("GlobalRequestsPerMinute must not exceed 100,000 for performance reasons");
        }

        // Validate specific endpoint rate limits
        if (options.QueryRequestsPerMinute <= 0)
        {
            errors.Add("QueryRequestsPerMinute must be greater than 0");
        }

        if (options.UploadRequestsPerMinute <= 0)
        {
            errors.Add("UploadRequestsPerMinute must be greater than 0");
        }

        if (options.MetadataRequestsPerMinute <= 0)
        {
            errors.Add("MetadataRequestsPerMinute must be greater than 0");
        }

        // Validate key prefix
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            errors.Add("KeyPrefix must not be empty");
        }

        // Validate status code
        if (options.StatusCode < 400 || options.StatusCode >= 600)
        {
            errors.Add("StatusCode must be a valid HTTP error status code (400-599)");
        }

        if (errors.Count > 0)
        {
            return ValidateOptionsResult.Fail(errors);
        }

        return ValidateOptionsResult.Success;
    }
}