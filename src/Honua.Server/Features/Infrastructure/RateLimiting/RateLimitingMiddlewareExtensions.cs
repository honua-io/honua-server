// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.RateLimiting;

public static class RateLimitingMiddlewareExtensions
{
    public static IServiceCollection AddRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure rate limiting options
        services.Configure<RateLimitingOptions>(
            configuration.GetSection(RateLimitingOptions.SectionName));

        // Register rate limit policy store
        services.TryAddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();

        // Validate options at startup
        services.AddSingleton<IValidateOptions<RateLimitingOptions>>(
            new RateLimitingOptionsValidator());

        return services;
    }

    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Resolve database-backed policies only after guarded startup has completed.
        // Lazy preserves one middleware instance for each pipeline branch.
        return app.Use(next =>
        {
            var middleware = new Lazy<RateLimitingMiddleware>(() =>
            {
                var services = app.ApplicationServices;
                return new RateLimitingMiddleware(next,
                    services.GetRequiredService<IRateLimitPolicyStore>(),
                    services.GetRequiredService<IOptions<RateLimitingOptions>>(),
                    services.GetRequiredService<ILogger<RateLimitingMiddleware>>(),
                    services.GetService<TimeProvider>() ?? TimeProvider.System);
            });
            return context => middleware.Value.InvokeAsync(context);
        });
    }
}

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
