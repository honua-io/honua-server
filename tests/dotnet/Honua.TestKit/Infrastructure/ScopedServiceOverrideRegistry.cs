// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.TestKit.Infrastructure;

internal sealed class ScopedServiceOverrideRegistry
{
    internal const string HeaderName = "X-Honua-Test-Service-Scope";

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<Type, object>> _scopes =
        new(StringComparer.Ordinal);

    internal void Add(string scopeId, IReadOnlyDictionary<Type, object> overrides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(overrides);

        // Publish an immutable snapshot. The fixture's local builder must not be able to
        // change an override set after it has been made visible to request processing.
        var snapshot = new Dictionary<Type, object>(overrides);
        if (!_scopes.TryAdd(scopeId, snapshot))
        {
            throw new InvalidOperationException($"A test service override scope named '{scopeId}' already exists.");
        }
    }

    internal void Remove(string scopeId) => _scopes.TryRemove(scopeId, out _);

    internal bool TryGet(string scopeId, out IReadOnlyDictionary<Type, object>? overrides)
        => _scopes.TryGetValue(scopeId, out overrides);
}

internal sealed class ScopedServiceOverrideStartupFilter(
    ScopedServiceOverrideRegistry registry) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Headers.TryGetValue(ScopedServiceOverrideRegistry.HeaderName, out var values) &&
                    values.Count == 1 &&
                    registry.TryGet(values[0]!, out var overrides) &&
                    overrides is not null)
                {
                    context.RequestServices = new ScopedServiceOverrideProvider(
                        context.RequestServices,
                        overrides);
                }

                await continuation(context);
            });
            next(app);
        };
}

internal sealed class ScopedServiceOverrideProvider(
    IServiceProvider inner,
    IReadOnlyDictionary<Type, object> overrides) : IServiceProvider
{
    public object? GetService(Type serviceType)
        => overrides.TryGetValue(serviceType, out var instance)
            ? instance
            : inner.GetService(serviceType);
}
