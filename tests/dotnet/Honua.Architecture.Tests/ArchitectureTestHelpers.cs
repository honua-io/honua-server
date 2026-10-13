// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.TestKit.Attributes;

namespace Honua.Architecture.Tests;

/// <summary>
/// Shared helpers for architecture test classes.
/// </summary>
internal static partial class ArchitectureTestHelpers
{
    private const BindingFlags TestMemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Type[] ApiCoverageAttributeTypes =
    [
        typeof(IntegrationTestAttribute),
        typeof(IntegrationTheoryAttribute),
        typeof(Honua.Worker.Gdal.Tests.GdalCliFactAttribute)
    ];

    /// <summary>
    /// Returns all types from an assembly, gracefully handling <see cref="ReflectionTypeLoadException"/>
    /// which can occur when optional dependencies are not present.
    /// </summary>
    internal static IEnumerable<Type> GetTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }

    /// <summary>
    /// Loads every assembly that may carry integration tests with endpoint /
    /// operation coverage attributes: the monolithic <c>Honua.Server.Tests</c>
    /// plus each extracted per-protocol test project
    /// (<c>Honua.Protocols.&lt;X&gt;.Tests</c>). The catalog generates explicit
    /// project references and a required assembly inventory. Missing assemblies
    /// fail discovery rather than silently reducing coverage.
    /// </summary>
    /// <remarks>
    /// Coverage scans must union these assemblies: as protocols are physically
    /// split out of Honua.Server, their integration tests move into the
    /// matching <c>Honua.Protocols.*.Tests</c> assembly. Anchoring on a single
    /// <c>Honua.Server.Tests</c> type would silently lose coverage for every
    /// extracted protocol.
    /// </remarks>
    internal static IReadOnlyList<Assembly> IntegrationTestAssemblies()
    {
        var baseDir = AppContext.BaseDirectory;
        var inventory = Path.Combine(baseDir, "test-coverage-assemblies.txt");
        if (!File.Exists(inventory))
        {
            throw new InvalidOperationException("Required test assembly catalog is missing from architecture output.");
        }

        var names = File.ReadAllLines(inventory).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Required test assembly catalog is empty.");
        }

        var assemblies = new List<Assembly>();
        foreach (var name in names)
        {
            var path = Path.Combine(baseDir, name);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Required coverage assembly is missing: {name}");
            }

            assemblies.Add(Assembly.LoadFrom(path));
        }

        return assemblies;
    }

    /// <summary>
    /// Returns true when the method or its declaring test class carries one of
    /// the test attributes that participates in API/operation coverage checks.
    /// </summary>
    internal static bool IsIntegrationTestMethod(Type type, MethodInfo method)
        => HasApiCoverageAttribute(type) || HasApiCoverageAttribute(method);

    /// <summary>
    /// Returns true when the type or any of its test methods carries one of the
    /// test attributes that participates in API/operation coverage checks.
    /// </summary>
    internal static bool IsIntegrationTestClass(Type type)
        => HasApiCoverageAttribute(type) ||
           type.GetMethods(TestMemberFlags).Any(method => IsIntegrationTestMethod(type, method));

    /// <summary>
    /// Enumerates every method across all integration-test assemblies
    /// (<see cref="IntegrationTestAssemblies"/>) that is marked with an integration
    /// category attribute or sits on a class marked with one. Endpoint- and
    /// operation-coverage scans share this discovery loop so the reflection
    /// traversal lives in one place.
    /// </summary>
    internal static IEnumerable<MethodInfo> IntegrationTestMethods()
    {
        foreach (var testAssembly in IntegrationTestAssemblies())
        {
            foreach (var type in GetTypesSafely(testAssembly))
            {
                foreach (var method in type.GetMethods(TestMemberFlags).Where(method => IsIntegrationTestMethod(type, method)))
                {
                    yield return method;
                }
            }
        }
    }

    private static bool HasApiCoverageAttribute(MemberInfo member)
        => member.CustomAttributes.Any(attribute =>
            ApiCoverageAttributeTypes.Any(type => attribute.AttributeType == type));

}
