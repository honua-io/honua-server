// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces dependency injection parameter limits to prevent over-injection anti-patterns.
/// Endpoints: max 5 parameters | Handlers: max 4 constructor parameters
/// Reference: CLAUDE.md Architecture Enforcement
/// </summary>
[Trait("Category", "Architecture")]
public sealed class DependencyInjectionLimitsTests
{
    private const int MaxEndpointParameters = 5;
    private const int MaxHandlerConstructorParameters = 4;

    [ArchitectureTest]
    public void EndpointHandlers_ShouldNotExceedParameterLimit()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;

        var violatingEndpoints = new List<string>();

        // Find all endpoint handler methods - these are typically private static methods
        // that handle HTTP requests in minimal APIs
        var endpointMethods = serverAssembly
            .GetTypes()
            .Where(type => type.Namespace?.Contains("Features") == true)
            .SelectMany(type => type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
            .Where(method => IsEndpointHandlerMethod(method))
            .ToArray();

        foreach (var method in endpointMethods)
        {
            var parameters = method.GetParameters();
            var injectedParameterCount = CountInjectedParameters(parameters);

            if (injectedParameterCount > MaxEndpointParameters)
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
        var serverAssembly = typeof(EndpointRegistry).Assembly;

        var violatingHandlers = new List<string>();

        // Find all handler classes - these typically end with "Handler" and are in Features directories
        var handlerTypes = serverAssembly
            .GetTypes()
            .Where(type => type.Name.EndsWith("Handler", StringComparison.OrdinalIgnoreCase) &&
                          type.Namespace?.Contains("Features") == true &&
                          !type.IsAbstract &&
                          !type.IsInterface)
            .ToArray();

        foreach (var handlerType in handlerTypes)
        {
            // Get the primary constructor (the one with the most parameters for record types,
            // or the only constructor for regular classes with DI)
            var constructors = handlerType.GetConstructors();
            var primaryConstructor = constructors
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (primaryConstructor != null)
            {
                var parameterCount = primaryConstructor.GetParameters().Length;

                if (parameterCount > MaxHandlerConstructorParameters)
                {
                    violatingHandlers.Add(
                        $"{handlerType.FullName} constructor has {parameterCount} parameters " +
                        $"(limit: {MaxHandlerConstructorParameters})");
                }
            }
        }

        violatingHandlers.Should().BeEmpty(
            $"Handler classes must not exceed {MaxHandlerConstructorParameters} constructor parameters to maintain " +
            "focused responsibility and prevent god object anti-pattern. " +
            "Consider breaking handlers into smaller, focused handlers or using composition.");
    }

    /// <summary>
    /// Determines if a method is an endpoint handler based on naming conventions and signature patterns.
    /// </summary>
    private static bool IsEndpointHandlerMethod(MethodInfo method)
    {
        // Endpoint handlers typically:
        // 1. Have "Handle" in their name
        // 2. Return Task<IResult> or IResult
        // 3. Are in endpoints classes (contain "Endpoint" in declaring type name)
        // 4. Take HttpContext as a parameter

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

            // Skip primitive types and common non-injected types that come from routing/query
            if (IsPrimitiveOrRouteParameter(paramType))
                continue;

            // Skip HttpContext - it's provided by the framework, not DI
            if (paramType == typeof(HttpContext))
                continue;

            // Skip CancellationToken - provided by framework
            if (paramType == typeof(CancellationToken))
                continue;

            // Everything else is likely an injected service
            injectedCount++;
        }

        return injectedCount;
    }

    /// <summary>
    /// Determines if a parameter type is a primitive or route parameter (not injected).
    /// </summary>
    private static bool IsPrimitiveOrRouteParameter(Type paramType)
    {
        // Primitive types and common route parameter types
        if (paramType.IsPrimitive || paramType == typeof(string) || paramType == typeof(Guid))
            return true;

        // Nullable primitive types
        if (Nullable.GetUnderlyingType(paramType)?.IsPrimitive == true)
            return true;

        // Nullable string, Guid, etc.
        var underlyingType = Nullable.GetUnderlyingType(paramType);
        if (underlyingType == typeof(string) || underlyingType == typeof(Guid))
            return true;

        // DateTime types commonly used in routing
        if (paramType == typeof(DateTime) || paramType == typeof(DateTimeOffset))
            return true;

        if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
            return true;

        return false;
    }
}
