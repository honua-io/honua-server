// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.GeoServices.GPServer;

internal static partial class GPServerEndpoints
{
    internal static void MapNAServerAsyncRoutingEndpoints(IEndpointRouteBuilder endpoints)
    {
        const string routingBase = "/rest/services/{serviceId}/NAServer/{taskName}";

        // These aliases share the GP authorization, durable job runtime and output mapping.
        // AllowAnonymous records that the handler enforces authorization before reading inputs.
        endpoints.MapPost($"{routingBase}/submitJob",
                static (HttpContext context, CancellationToken ct) => HandleRoutingSubmit(context, ct))
            .WithName("NAServerRoutingSubmitJob").WithDisplayName("NAServer Routing Submit Job")
            .WithSummary("Submit an asynchronous routing web tool").WithTags("NAServer").AllowAnonymous();
        endpoints.MapGet($"{routingBase}/submitJob",
                static (HttpContext context, CancellationToken ct) => HandleRoutingSubmit(context, ct))
            .WithName("NAServerRoutingSubmitJobGet").WithDisplayName("NAServer Routing Submit Job (GET)")
            .WithSummary("Submit an asynchronous routing web tool using GET").WithTags("NAServer")
            .WithMetadata(new HeadRequestRejectedEndpointMetadata([HttpMethods.Get, HttpMethods.Post]));
        endpoints.MapGet($"{routingBase}/jobs/{{jobId}}",
                static (HttpContext context, CancellationToken ct) => HandleRoutingAlias(context, HandleJobStatus, ct))
            .WithName("NAServerRoutingJobStatus").WithDisplayName("NAServer Routing Job Status")
            .WithSummary("Get asynchronous routing job status").WithTags("NAServer");
        endpoints.MapGet($"{routingBase}/jobs/{{jobId}}/results/{{paramName}}",
                static (HttpContext context, CancellationToken ct) => HandleRoutingAlias(context, HandleJobResult, ct))
            .WithName("NAServerRoutingJobResult").WithDisplayName("NAServer Routing Job Result")
            .WithSummary("Get a named routing job result").WithTags("NAServer");
    }

    private static Task<IResult> HandleRoutingSubmit(HttpContext context, CancellationToken ct)
        => HandleRoutingAlias(context, static (request, token) => HandleSubmitJob(request, token), ct);

    private static Task<IResult> HandleRoutingAlias(HttpContext context,
        Func<HttpContext, CancellationToken, Task<IResult>> handler, CancellationToken ct)
        => RoutingAsyncWebTools.IsTask(context.Request.RouteValues["taskName"]?.ToString())
            ? handler(context, ct)
            : Task.FromResult(StandardErrorHelpers.CreateNotFound(context, "Routing web tool was not found."));
}
