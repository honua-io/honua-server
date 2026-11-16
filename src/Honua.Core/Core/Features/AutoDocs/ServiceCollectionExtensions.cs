// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AutoDocs.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.AutoDocs;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAutoDocsCore(this IServiceCollection services)
    {
        services.TryAddSingleton<MetadataDocumentGenerator>();
        return services;
    }
}
