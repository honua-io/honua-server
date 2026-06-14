// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

        return services;
    }
}
