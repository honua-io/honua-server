// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

// The admin sign-in endpoints admit five requests a minute per client address and block a source
// after repeated failures. A host shared by many cases gives each case's client an address of its own.
internal static class PerClientAddress
{
    private const string Header = "X-Test-Client-Address";

    // Register after any helper that pins a fixed per-host address, so this one wins.
    internal static void Register(IServiceCollection services) =>
        services.AddSingleton<IStartupFilter>(new AddressFilter());

    internal static HttpClient Assign(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(Header, Guid.NewGuid().ToString("N"));
        return client;
    }

    private sealed class AddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use((HttpContext context, RequestDelegate continueRequest) =>
            {
                if (Guid.TryParse(context.Request.Headers[Header], out var client))
                {
                    context.Connection.RemoteIpAddress = new IPAddress(client.ToByteArray());
                }

                return continueRequest(context);
            });
            next(application);
        };
    }
}
