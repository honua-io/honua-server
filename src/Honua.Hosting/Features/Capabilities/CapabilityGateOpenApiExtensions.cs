// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Capabilities;

internal static class CapabilityGateOpenApiExtensions
{
    internal static OpenApiOptions AddCapabilityGate(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer<CapabilityGateOpenApiDocumentTransformer>();
        return options;
    }

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
