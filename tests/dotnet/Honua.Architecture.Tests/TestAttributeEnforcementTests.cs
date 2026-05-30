// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ensures all integration tests have proper attributes for discoverability and organization.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class TestAttributeEnforcementTests
{
    [ArchitectureTest]
    public void AllIntegrationTestClasses_MustHaveProtocolAttribute()
    {
        var testAssembly = typeof(Honua.Server.Tests.Import.UniversalProgressStoreIntegrationTests).Assembly;
        var classesWithoutProtocol = new List<string>();

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            // Skip if not a test class
            if (!IsIntegrationTestClass(type))
            {
                continue;
            }

            var classProtocols = GetProtocolValues(type);
            if (classProtocols.Length > 0)
            {
                continue;
            }

            var hasMethodProtocol = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(method => IsIntegrationTestMethod(type, method) && GetProtocolValues(method).Length > 0);

            if (!hasMethodProtocol)
            {
                classesWithoutProtocol.Add($"{type.FullName} - Missing [Protocol] attribute on class");
            }
        }

        classesWithoutProtocol.Should().BeEmpty(
            $"All integration test classes must have [Protocol] attribute for discoverability. " +
            $"Missing attributes:\n{string.Join("\n", classesWithoutProtocol)}");
    }

    [ArchitectureTest]
    public void AllIntegrationTestMethods_MustHaveOperationAndEndpointAttributes()
    {
        var testAssembly = typeof(Honua.Server.Tests.Import.UniversalProgressStoreIntegrationTests).Assembly;
        var methodsWithMissingAttributes = new List<string>();

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            var classOperations = GetOperationValues(type);
            var classProtocols = GetProtocolValues(type);

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsIntegrationTestMethod(type, method))
                {
                    continue;
                }

                var methodOperations = GetOperationValues(method);
                var effectiveOperations = methodOperations.Length > 0 ? methodOperations : classOperations;

                if (effectiveOperations.Length == 0)
                {
                    methodsWithMissingAttributes.Add($"{type.Name}.{method.Name} - Missing [Operation] attribute");
                }

                var methodProtocols = GetProtocolValues(method);
                var effectiveProtocols = classProtocols.Concat(methodProtocols).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var requiresEndpoint = !effectiveProtocols.Contains(ProtocolNames.TestQuality, StringComparer.OrdinalIgnoreCase) &&
                    !effectiveOperations.Any(operation => _operationsWithoutEndpointRequirement.Contains(operation));

                if (requiresEndpoint)
                {
                    var endpointAttributes = method.GetCustomAttributes(typeof(EndpointAttribute), inherit: true);
                    if (endpointAttributes.Length == 0)
                    {
                        methodsWithMissingAttributes.Add($"{type.Name}.{method.Name} - Missing [Endpoint] attribute");
                    }
                }
            }
        }

        methodsWithMissingAttributes.Should().BeEmpty(
            $"All integration test methods must have [Operation] and [Endpoint] attributes for discoverability. " +
            $"Missing attributes:\n{string.Join("\n", methodsWithMissingAttributes)}");
    }

    [ArchitectureTest]
    public void AllProtocolAttributes_ShouldUseStandardizedValues()
    {
        var testAssembly = typeof(Honua.Server.Tests.Import.UniversalProgressStoreIntegrationTests).Assembly;
        var invalidProtocolValues = new List<string>();

        var validProtocols = GetConstantValues(typeof(ProtocolNames));

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            foreach (var protocolAttribute in type.GetCustomAttributes(typeof(ProtocolAttribute), inherit: true).Cast<ProtocolAttribute>())
            {
                foreach (var protocol in protocolAttribute.Protocols)
                {
                    if (string.IsNullOrWhiteSpace(protocol))
                    {
                        invalidProtocolValues.Add($"{type.Name} - Protocol value cannot be empty.");
                        continue;
                    }

                    if (!validProtocols.Contains(protocol))
                    {
                        invalidProtocolValues.Add(
                            $"{type.Name} - Invalid protocol '{protocol}'. " +
                            $"Valid values: {string.Join(", ", validProtocols.OrderBy(p => p))}");
                    }
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var protocolAttribute in method.GetCustomAttributes(typeof(ProtocolAttribute), inherit: true).Cast<ProtocolAttribute>())
                {
                    foreach (var protocol in protocolAttribute.Protocols)
                    {
                        if (string.IsNullOrWhiteSpace(protocol))
                        {
                            invalidProtocolValues.Add($"{type.Name}.{method.Name} - Protocol value cannot be empty.");
                            continue;
                        }

                        if (!validProtocols.Contains(protocol))
                        {
                            invalidProtocolValues.Add(
                                $"{type.Name}.{method.Name} - Invalid protocol '{protocol}'. " +
                                $"Valid values: {string.Join(", ", validProtocols.OrderBy(p => p))}");
                        }
                    }
                }
            }
        }

        invalidProtocolValues.Should().BeEmpty(
            $"All [Protocol] attributes should use standardized values. " +
            $"Invalid values found:\n{string.Join("\n", invalidProtocolValues)}");
    }

    [ArchitectureTest]
    public void AllOperationAttributes_ShouldUseStandardizedValues()
    {
        var testAssembly = typeof(Honua.Server.Tests.Import.UniversalProgressStoreIntegrationTests).Assembly;
        var invalidOperationValues = new List<string>();

        var validOperations = GetConstantValues(typeof(Operations));

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            foreach (var operationAttribute in type.GetCustomAttributes(typeof(OperationAttribute), inherit: true).Cast<OperationAttribute>())
            {
                foreach (var operation in operationAttribute.Operations)
                {
                    if (string.IsNullOrWhiteSpace(operation))
                    {
                        invalidOperationValues.Add($"{type.Name} - Operation value cannot be empty.");
                        continue;
                    }

                    if (!validOperations.Contains(operation))
                    {
                        invalidOperationValues.Add(
                            $"{type.Name} - Invalid operation '{operation}'. " +
                            $"Valid values: {string.Join(", ", validOperations.OrderBy(o => o))}");
                    }
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var operationAttribute in method.GetCustomAttributes(typeof(OperationAttribute), inherit: true).Cast<OperationAttribute>())
                {
                    foreach (var operation in operationAttribute.Operations)
                    {
                        if (string.IsNullOrWhiteSpace(operation))
                        {
                            invalidOperationValues.Add($"{type.Name}.{method.Name} - Operation value cannot be empty.");
                            continue;
                        }

                        if (!validOperations.Contains(operation))
                        {
                            invalidOperationValues.Add(
                                $"{type.Name}.{method.Name} - Invalid operation '{operation}'. " +
                                $"Valid values: {string.Join(", ", validOperations.OrderBy(o => o))}");
                        }
                    }
                }
            }
        }

        invalidOperationValues.Should().BeEmpty(
            $"All [Operation] attributes should use standardized values. " +
            $"Invalid values found:\n{string.Join("\n", invalidOperationValues)}");
    }

    [ArchitectureTest]
    public void AllEndpointAttributes_ShouldHaveValidFormat()
    {
        var testAssembly = typeof(Honua.Server.Tests.Import.UniversalProgressStoreIntegrationTests).Assembly;
        var invalidEndpointFormats = new List<string>();

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var endpointAttribute in method.GetCustomAttributes(typeof(EndpointAttribute), inherit: true).Cast<EndpointAttribute>())
                {
                    var endpoint = endpointAttribute.Endpoint.Trim();

                    // Should be in format "METHOD /path"
                    var parts = endpoint.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2)
                    {
                        invalidEndpointFormats.Add(
                            $"{type.Name}.{method.Name} - Invalid endpoint format '{endpoint}'. " +
                            $"Expected format: 'METHOD /path' (e.g., 'GET /api/v1/test')");
                        continue;
                    }

                    var method_part = parts[0].ToUpperInvariant();
                    var path_part = parts[1];

                    // Validate HTTP method
                    var validMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
                    if (!validMethods.Contains(method_part))
                    {
                        invalidEndpointFormats.Add(
                            $"{type.Name}.{method.Name} - Invalid HTTP method '{method_part}'. " +
                            $"Valid methods: {string.Join(", ", validMethods)}");
                    }

                    // Validate path starts with /
                    if (!path_part.StartsWith('/'))
                    {
                        invalidEndpointFormats.Add(
                            $"{type.Name}.{method.Name} - Path must start with '/'. Got: '{path_part}'");
                    }
                }
            }
        }

        invalidEndpointFormats.Should().BeEmpty(
            $"All [Endpoint] attributes should have valid format 'METHOD /path'. " +
            $"Invalid formats found:\n{string.Join("\n", invalidEndpointFormats)}");
    }

    [ArchitectureTest]
    public void AllInterfaceOperationAttributes_ShouldUseRegisteredValues()
    {
        var testAssembly = typeof(Honua.Server.Tests.Import.UniversalProgressStoreIntegrationTests).Assembly;
        var invalidValues = new List<string>();

        var registeredOperations = OperationRegistry.All
            .Select(op => $"{op.Protocol}::{op.Operation}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var attr in method.GetCustomAttributes(typeof(InterfaceOperationAttribute), inherit: true).Cast<InterfaceOperationAttribute>())
                {
                    var key = $"{attr.Protocol}::{attr.Operation}";
                    if (!registeredOperations.Contains(key))
                    {
                        invalidValues.Add(
                            $"{type.Name}.{method.Name} - [InterfaceOperation(\"{attr.Protocol}\", \"{attr.Operation}\")] " +
                            $"does not match any entry in OperationRegistry.All. " +
                            $"Valid entries: {string.Join(", ", registeredOperations.OrderBy(k => k))}");
                    }
                }
            }
        }

        invalidValues.Should().BeEmpty(
            "all [InterfaceOperation] attribute values must match entries in OperationRegistry.All. " +
            $"Invalid values found:\n{string.Join("\n", invalidValues)}");
    }

    private static bool IsIntegrationTestClass(Type type)
    {
        // Check if class has [IntegrationTest] attribute
        if (type.GetCustomAttributes(typeof(IntegrationTestAttribute), inherit: true).Length > 0)
        {
            return true;
        }

        // Check if any method has [IntegrationTest] attribute
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(method => method.GetCustomAttributes(typeof(IntegrationTestAttribute), inherit: true).Length > 0);
    }

    private static bool IsIntegrationTestMethod(Type type, MethodInfo method)
    {
        var classHasIntegration = type.GetCustomAttributes(typeof(IntegrationTestAttribute), inherit: true).Length > 0;
        var methodHasIntegration = method.GetCustomAttributes(typeof(IntegrationTestAttribute), inherit: true).Length > 0;
        return classHasIntegration || methodHasIntegration;
    }

    private static string[] GetProtocolValues(MemberInfo member)
    {
        return member.GetCustomAttributes(typeof(ProtocolAttribute), inherit: true)
            .Cast<ProtocolAttribute>()
            .SelectMany(attribute => attribute.Protocols)
            .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
            .ToArray();
    }

    private static string[] GetOperationValues(MemberInfo member)
    {
        return member.GetCustomAttributes(typeof(OperationAttribute), inherit: true)
            .Cast<OperationAttribute>()
            .SelectMany(attribute => attribute.Operations)
            .Where(operation => !string.IsNullOrWhiteSpace(operation))
            .ToArray();
    }

    private static HashSet<string> GetConstantValues(Type type)
    {
        return type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> _operationsWithoutEndpointRequirement = new(StringComparer.OrdinalIgnoreCase)
    {
        Operations.TestQuality,
        Operations.TestInfrastructure,
        Operations.FuzzTesting,
        Operations.SecurityTesting,
        Operations.ChaosTesting,
        Operations.ContractTesting,
        Operations.PerformanceTesting
    };
}
