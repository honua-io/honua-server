// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Capabilities;

/// <summary>
/// Registration helpers that wire the <see cref="CapabilityGateOpenApiDocumentTransformer"/>
/// into the <c>AddOpenApi(...)</c> pipeline (Track T7 / #2343) so disabled-experimental
/// endpoints are pruned from the generated OpenAPI document, keeping the published
/// contract in agreement with the runtime capability gate (Track T5).
/// </summary>
internal static class CapabilityGateOpenApiExtensions
{
    /// <summary>
    /// Adds the capability-gate document transformer to an OpenAPI document's
    /// transformer pipeline. Apply inside an <c>AddOpenApi</c> configuration callback:
    /// <code>
    /// builder.Services.AddOpenApi(options => options.AddCapabilityGate());
    /// </code>
    /// </summary>
    /// <param name="options">The OpenAPI document options being configured.</param>
    /// <returns>The same <see cref="OpenApiOptions"/> for chaining.</returns>
    internal static OpenApiOptions AddCapabilityGate(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer<CapabilityGateOpenApiDocumentTransformer>();
        return options;
    }

    /// <summary>
    /// Registers an OpenAPI document whose transformer pipeline prunes
    /// disabled-experimental operations via the capability gate. Convenience wrapper
    /// over <c>AddOpenApi(documentName, options =&gt; options.AddCapabilityGate())</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="documentName">The OpenAPI document name (default <c>v1</c>).</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    internal static IServiceCollection AddCapabilityGatedOpenApi(
        this IServiceCollection services,
        string documentName = "v1")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(documentName);

        services.AddOpenApi(documentName, options => options.AddCapabilityGate());
        return services;
    }
}
