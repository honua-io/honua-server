// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.TestKit;

// One host for a serialized test class whose cases differ only in the substitute they register for
// TService. The host registers a ServiceSwitch in its place; a case borrows the host with its own
// substitute and returns it when done. Borrow and return both restore the seeded catalog under a
// new revision, so a response cached while one case's substitute answered is never served to another.
public class SwitchedServiceHost<TService> : SeededMetadataHost
    where TService : class
{
    private readonly ServiceSwitch<TService> _services;
    private readonly TService _defaultService;

    public SwitchedServiceHost(TService defaultService)
        : this(CreateSwitch(defaultService), static _ => { })
    {
    }

    public SwitchedServiceHost(TService defaultService, Action<IServiceCollection> configureServices)
        : this(CreateSwitch(defaultService), configureServices)
    {
    }

    private SwitchedServiceHost(ServiceSwitch<TService> services, Action<IServiceCollection> configureServices)
        : base(new WebAppFixture().ConfigureServices(collection =>
        {
            configureServices(collection);
            collection.AddSingleton(services.Service);
        }))
    {
        _services = services;
        _defaultService = services.Target;
    }

    private static ServiceSwitch<TService> CreateSwitch(TService defaultService)
    {
        var services = (ServiceSwitch<TService>)(object)DispatchProxy.Create<TService, ServiceSwitch<TService>>();
        services.Target = defaultService;
        return services;
    }

    public async Task<SwitchedServiceLease> BorrowAsync(TService service)
    {
        _services.Target = service;
        await RestoreSeededStateAsync();
        return new SwitchedServiceLease(Application, ReturnAsync);
    }

    private async ValueTask ReturnAsync()
    {
        _services.Target = _defaultService;
        await RestoreSeededStateAsync();
    }
}

// The borrowed host as a case sees it; disposing the lease returns the host instead of stopping it.
public sealed class SwitchedServiceLease : IAsyncDisposable
{
    private readonly WebAppFixture _application;
    private readonly Func<ValueTask> _return;

    internal SwitchedServiceLease(WebAppFixture application, Func<ValueTask> returnHost)
    {
        _application = application;
        _return = returnHost;
    }

    public HttpClient Client => _application.Client;

    public T GetService<T>()
        where T : notnull => _application.GetService<T>();

    public ValueTask DisposeAsync() => _return();
}
