// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces dependency injection parameter limits to prevent over-injection anti-patterns.
/// Endpoints: max 5 parameters | Handlers/Coordinators/Managers: max 4 collaborators.
/// The checks run across every production Honua assembly (server, protocol adapters, and
/// providers), not just <c>Honua.Server</c>, so SRP fan-out is enforced everywhere the rule
/// claims to apply. A small, explicit allowlist documents pre-existing exceptions so the
/// rule ratchets: it blocks new violations while tracking known debt.
/// Reference: AGENTS.md Architecture Enforcement.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class DependencyInjectionLimitsTests
{
    private const int MaxEndpointParameters = 5;
    private const int MaxHandlerConstructorParameters = 4;

    /// <summary>
    /// Service/coordinator/facade types are legitimately composed of more collaborators than a
    /// single request handler, so they are held to a looser "god object" ceiling. This still
    /// catches the 15+ dependency aggregates the handler limit is meant to flag while not
    /// failing every normally-sized service.
    /// </summary>
    private const int MaxServiceCollaborators = 8;

    /// <summary>
    /// Pre-existing endpoint over-injection accepted as tracked debt (raster tile render paths
    /// that legitimately compose several render/cache services). Do not grow this for new code.
    /// </summary>
    private static readonly HashSet<string> AllowedEndpointExceptions = new(StringComparer.Ordinal)
    {
        "Honua.Protocols.Ogc.Api.Tiles.TilesEndpoints.HandleRasterTileAsync",
        "Honua.Protocols.Ogc.Api.Tiles.TilesEndpoints.HandleDatasetRasterTileAsync",
    };

    /// <summary>
    /// Pre-existing handler/coordinator/manager over-injection accepted as tracked debt.
    /// Add a type here only with a follow-up issue; do not grow this list for new code.
    /// </summary>
    private static readonly HashSet<string> AllowedHandlerExceptions = new(StringComparer.Ordinal)
    {
        "Honua.Protocols.GeoServices.ImageServer.Handlers.ImageServerProjectHandler",
    };

    /// <summary>
    /// Service/coordinator/facade god objects accepted as tracked debt. Now empty: the last
    /// allowlisted aggregate (GeoservicesImportService) was decomposed so the gate enforces the
    /// collaborator ceiling on every service with no exceptions. Add a type here only with a
    /// follow-up issue; do not grow this list for new code.
    /// </summary>
    private static readonly HashSet<string> AllowedServiceExceptions = new(StringComparer.Ordinal);

    [ArchitectureTest]
    public void EndpointHandlers_ShouldNotExceedParameterLimit()
    {
        var violatingEndpoints = new List<string>();

        var endpointMethods = ProductionTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
            .Where(IsEndpointHandlerMethod);

        foreach (var method in endpointMethods)
        {
            var parameters = method.GetParameters();
            var injectedParameterCount = CountInjectedParameters(parameters);
            var endpointId = $"{method.DeclaringType?.FullName}.{method.Name}";

            if (injectedParameterCount > MaxEndpointParameters &&
                !AllowedEndpointExceptions.Contains(endpointId))
            {
                violatingEndpoints.Add(
                    $"{method.DeclaringType?.FullName}.{method.Name} has {injectedParameterCount} injected parameters " +
                    $"(limit: {MaxEndpointParameters})");
            }
        }

        violatingEndpoints.Should().BeEmpty(
            $"Endpoint handlers must not exceed {MaxEndpointParameters} injected parameters to prevent over-injection anti-pattern. " +
            "Consider refactoring to use a service facade or breaking down responsibilities.");
    }

    [ArchitectureTest]
    public void HandlerClasses_ShouldNotExceedConstructorParameterLimit()
    {
        var violatingHandlers = new List<string>();

        var handlerTypes = ProductionTypes()
            .Where(type => IsCandidateType(type) && HasHandlerSuffix(type.Name));

        foreach (var handlerType in handlerTypes)
        {
            var collaborators = CountConstructorCollaborators(handlerType);
            if (collaborators > MaxHandlerConstructorParameters &&
                !AllowedHandlerExceptions.Contains(handlerType.FullName!))
            {
                violatingHandlers.Add(
                    $"{handlerType.FullName} constructor has {collaborators} collaborators " +
                    $"(limit: {MaxHandlerConstructorParameters})");
            }
        }

        violatingHandlers.Should().BeEmpty(
            $"Handler/coordinator/manager classes must not exceed {MaxHandlerConstructorParameters} injected collaborators to maintain " +
            "focused responsibility and prevent god object anti-pattern. " +
            "Consider breaking handlers into smaller, focused handlers or using composition.");
    }

    [ArchitectureTest]
    public void ServiceClasses_ShouldNotBecomeGodObjects()
    {
        var violatingServices = new List<string>();

        // The god-object ceiling now covers the common high-fan-out construct names, not just
        // *Service/*Services/*Facade, so the gate cannot be evaded by naming an over-injected type
        // *Pipeline, *Engine, *Runner, *Builder, *Provider, *Registry, *Store, *Dispatcher, etc.
        // Records/value carriers are excluded (their constructor parameters are data, not DI
        // collaborators), and types caught by the stricter handler ceiling are reported there.
        var serviceTypes = ProductionTypes()
            .Where(type => IsCandidateType(type)
                && !IsRecordType(type)
                && !HasHandlerSuffix(type.Name)
                && HasServiceLikeName(type.Name));

        foreach (var serviceType in serviceTypes)
        {
            var collaborators = CountConstructorCollaborators(serviceType);
            if (collaborators > MaxServiceCollaborators &&
                !AllowedServiceExceptions.Contains(serviceType.FullName!))
            {
                violatingServices.Add(
                    $"{serviceType.FullName} constructor has {collaborators} collaborators " +
                    $"(limit: {MaxServiceCollaborators})");
            }
        }

        violatingServices.Should().BeEmpty(
            $"Service/coordinator/facade classes must not exceed {MaxServiceCollaborators} injected collaborators (god object anti-pattern). " +
            "Collapse option bundles into a snapshot, remove service-locator (IServiceProvider) injection, and split per-domain responsibilities.");
    }

    /// <summary>
    /// Loads every production Honua assembly that landed in the test output directory and yields
    /// its loadable types. Test, benchmark, and test-kit assemblies are excluded.
    /// </summary>
    private static IEnumerable<Type> ProductionTypes()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
        {
            [typeof(EndpointRegistry).Assembly.GetName().Name!] = typeof(EndpointRegistry).Assembly
        };

        foreach (var path in Directory.EnumerateFiles(baseDirectory, "Honua.*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (IsNonProductionAssembly(name) || loaded.ContainsKey(name))
            {
                continue;
            }

            try
            {
                loaded[name] = Assembly.LoadFrom(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Native/unmanaged or otherwise unloadable assemblies are not inspected.
            }
        }

        foreach (var assembly in loaded.Values)
        {
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (var type in types)
            {
                if (type is not null)
                {
                    yield return type;
                }
            }
        }
    }

    private static bool IsNonProductionAssembly(string assemblyName)
        => assemblyName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || assemblyName.Contains("TestKit", StringComparison.OrdinalIgnoreCase)
            || assemblyName.Contains("Benchmarks", StringComparison.OrdinalIgnoreCase);

    private static bool IsCandidateType(Type type)
        => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            && type.Namespace?.StartsWith("Honua.", StringComparison.Ordinal) == true;

    private static bool HasHandlerSuffix(string typeName)
        => typeName.EndsWith("Handler", StringComparison.Ordinal)
            || typeName.EndsWith("Coordinator", StringComparison.Ordinal)
            || typeName.EndsWith("Manager", StringComparison.Ordinal);

    /// <summary>
    /// Behavioral construct names held to the god-object collaborator ceiling. Beyond the classic
    /// <c>Service</c>/<c>Facade</c> suffixes this covers the common aliases a high-fan-out type could
    /// otherwise hide behind (<c>Pipeline</c>, <c>Engine</c>, <c>Runner</c>, <c>Dispatcher</c>, etc.)
    /// so the ratchet cannot be evaded by renaming. Data-carrier suffixes are intentionally excluded.
    /// </summary>
    private static readonly string[] ServiceLikeSuffixes =
    {
        "Service", "Services", "Facade", "Pipeline", "Engine", "Runner", "Dispatcher",
        "Processor", "Orchestrator", "Scheduler", "Executor", "Worker", "Provider",
        "Registry", "Store", "Builder", "Factory", "Gateway", "Broker",
    };

    private static bool HasServiceLikeName(string typeName)
    {
        foreach (var suffix in ServiceLikeSuffixes)
        {
            if (typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects C# records (and record structs) by their compiler-synthesized clone member so that
    /// value/DTO carriers with many positional parameters are not mistaken for over-injected services.
    /// </summary>
    private static bool IsRecordType(Type type)
        => type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null
            || type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic) is not null;

    /// <summary>
    /// Counts the real collaborators a type injects, excluding configuration and logging
    /// plumbing (IOptions, ILogger) and primitive/route values so the count reflects genuine
    /// dependency fan-out rather than noise.
    /// </summary>
    private static int CountConstructorCollaborators(Type type)
    {
        var primaryConstructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (primaryConstructor is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var parameter in primaryConstructor.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            if (IsPrimitiveOrRouteParameter(parameterType) ||
                parameterType == typeof(CancellationToken) ||
                IsLoggerType(parameterType) ||
                IsOptionsType(parameterType))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static bool IsLoggerType(Type type)
    {
        if (type == typeof(ILogger))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>);
    }

    private static bool IsOptionsType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IOptions<>)
            || definition == typeof(IOptionsSnapshot<>)
            || definition == typeof(IOptionsMonitor<>);
    }

    /// <summary>
    /// Determines if a method is an endpoint handler based on naming conventions and signature patterns.
    /// </summary>
    private static bool IsEndpointHandlerMethod(MethodInfo method)
    {
        var methodName = method.Name;
        var returnType = method.ReturnType;
        var declaringTypeName = method.DeclaringType?.Name ?? "";
        var parameters = method.GetParameters();

        var hasHandleInName = methodName.Contains("Handle", StringComparison.OrdinalIgnoreCase);
        var hasCorrectReturnType = returnType == typeof(IResult) ||
                                  returnType == typeof(Task<IResult>) ||
                                  returnType == typeof(ValueTask<IResult>);
        var isInEndpointsClass = declaringTypeName.Contains("Endpoint", StringComparison.OrdinalIgnoreCase);
        var hasHttpContext = parameters.Any(p => p.ParameterType == typeof(HttpContext));

        return hasHandleInName && hasCorrectReturnType && isInEndpointsClass && hasHttpContext;
    }

    /// <summary>
    /// Counts parameters that are injected via DI, excluding route parameters and query parameters.
    /// </summary>
    private static int CountInjectedParameters(ParameterInfo[] parameters)
    {
        var injectedCount = 0;

        foreach (var param in parameters)
        {
            var paramType = param.ParameterType;

            if (IsPrimitiveOrRouteParameter(paramType))
                continue;

            if (paramType == typeof(HttpContext))
                continue;

            if (paramType == typeof(CancellationToken))
                continue;

            if (typeof(Delegate).IsAssignableFrom(paramType))
                continue;

            injectedCount++;
        }

        return injectedCount;
    }

    /// <summary>
    /// Determines if a parameter type is a primitive or route parameter (not injected).
    /// </summary>
    private static bool IsPrimitiveOrRouteParameter(Type paramType)
    {
        if (paramType.IsPrimitive || paramType == typeof(string) || paramType == typeof(Guid) || paramType.IsEnum)
            return true;

        if (Nullable.GetUnderlyingType(paramType)?.IsPrimitive == true)
            return true;

        var underlyingType = Nullable.GetUnderlyingType(paramType);
        if (underlyingType == typeof(string) || underlyingType == typeof(Guid) || underlyingType?.IsEnum == true)
            return true;

        if (paramType == typeof(DateTime) || paramType == typeof(DateTimeOffset))
            return true;

        if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
            return true;

        return false;
    }
}
