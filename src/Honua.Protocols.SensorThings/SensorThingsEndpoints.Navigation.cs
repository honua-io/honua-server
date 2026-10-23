// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.SensorThings;

internal static partial class SensorThingsEndpoints
{

    private static void ConfigureNavigation(RouteHandlerBuilder builder, string name) =>
        builder.WithDisplayName($"STA {name} Navigation")
            .WithName($"Sta{name}Navigation")
            .WithSummary("Get related SensorThings entities")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(400)
            .Produces(404)
            .Produces(501);

}
