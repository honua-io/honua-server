// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Security.Admission;

public static class AiExecutionAdmissionRegistration
{
    public static IServiceCollection AddAiExecutionAdmission(this IServiceCollection services, IConfiguration configuration)
    {
        var legacyBackend = configuration[$"{AiExecutionAdmissionOptions.SectionName}:Backend"];
        if (legacyBackend is not null &&
            !string.Equals(legacyBackend, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(legacyBackend, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AI admission requires PostgreSQL. Remove the retired AiExecutionAdmission:Backend setting after migrating its ledger.");
        }

        services.AddOptions<AiExecutionAdmissionOptions>()
            .Bind(configuration.GetSection(AiExecutionAdmissionOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAiExecutionAdmission, AiExecutionAdmissionCoordinator>();
        services.TryAddSingleton<AiExecutionContextFactory>();
        return services;
    }
}
