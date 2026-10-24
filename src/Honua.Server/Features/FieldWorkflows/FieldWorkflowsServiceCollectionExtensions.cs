// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.FieldWorkflows.Export;
using Honua.Server.Features.FieldWorkflows.Review;

namespace Honua.Server.Features.FieldWorkflows;

/// <summary>
/// Registers back-office field workflow services (review/QA, exports). The
/// durable stores are provided by the active data provider (Postgres).
/// </summary>
internal static class FieldWorkflowsServiceCollectionExtensions
{
    public static IServiceCollection AddFieldWorkflows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<FieldReviewService>();
        services.AddScoped<FieldExportService>();

        return services;
    }
}
