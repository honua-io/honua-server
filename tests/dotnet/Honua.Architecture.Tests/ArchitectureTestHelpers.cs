// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.TestKit.Attributes;

namespace Honua.Architecture.Tests;

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

    internal static bool IsIntegrationTestMethod(Type type, MethodInfo method)
        => HasApiCoverageAttribute(type) || HasApiCoverageAttribute(method);

    internal static bool IsIntegrationTestClass(Type type)
        => HasApiCoverageAttribute(type) ||
           type.GetMethods(TestMemberFlags).Any(method => IsIntegrationTestMethod(type, method));

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
