// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Configuration;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

internal static class GeometryServiceTestContexts
{
    public const string GeometryServer = "/rest/services/Utilities/Geometry/GeometryServer";

    public static DefaultHttpContext Create(
        GeometryServiceCaller caller,
        string method = "GET",
        GeometryServiceAdmission? admission = null)
    {
        var services = Services(new LimitsOptions());
        if (admission is not null)
        {
            services.AddSingleton(admission);
        }

        return SignIn(Build(services, method), caller);
    }

    public static DefaultHttpContext Create(GeometryServiceCaller caller, LimitsOptions limits)
        => SignIn(Build(Services(limits), "GET"), caller);

    public static DefaultHttpContext CreateWithoutLimits() => Build(new ServiceCollection(), "GET");

    private static ServiceCollection Services(LimitsOptions limits)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<LimitsOptions>>(Options.Create(limits));
        return services;
    }

    private static DefaultHttpContext Build(ServiceCollection services, string method)
    {
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = GeometryServer + "/densify";
        return context;
    }

    private static DefaultHttpContext SignIn(DefaultHttpContext context, GeometryServiceCaller caller)
    {
        if (caller == GeometryServiceCaller.SignedIn)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "editor")], "test"));
        }

        return context;
    }
}
