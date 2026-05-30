// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal static class OperateObservabilityFixtureEndpoints
{
    public static void MapOperateObservabilityFixtureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<OperateObservabilityFixtureOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/dev/fixtures")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Fixtures", "Observability")
            .RequireAdminAuthorization();

        group.MapPost("/operate-observability/{profile}", HandleSeed)
            .WithDisplayName("Seed Operate Observability Fixture")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleSeed(
        string profile,
        [FromServices] IOperateObservabilityFixtureSeeder seeder,
        [FromServices] IOptions<OperateObservabilityFixtureOptions> options,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(profile, options.Value.Profile, StringComparison.Ordinal))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operate observability fixture profile '{profile}' was not found.");
        }

        var response = await seeder.SeedAsync(profile, cancellationToken).ConfigureAwait(false);
        return Results.Json(response, OperateObservabilityFixtureJsonContext.Default.OperateObservabilityFixtureSeedResponse);
    }
}
