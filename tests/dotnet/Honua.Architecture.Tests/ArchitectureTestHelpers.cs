// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Xml.Linq;
using Honua.TestKit.Attributes;

namespace Honua.Architecture.Tests;

/// <summary>
/// Shared helpers for architecture test classes.
/// </summary>
internal static class ArchitectureTestHelpers
{
    private const BindingFlags TestMemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Type[] IntegrationAttributeTypes =
    [
        typeof(IntegrationTestAttribute),
        typeof(CloudTestAttribute),
        typeof(EmulatorTestAttribute),
        typeof(ExternalServiceTestAttribute),
        typeof(RoutingTestAttribute),
        typeof(ScaleTestAttribute)
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
    /// (<c>Honua.Protocols.&lt;X&gt;.Tests</c>). They are discovered by globbing
    /// the test output directory, so a newly extracted protocol test project is
    /// picked up automatically once Architecture.Tests references it (which puts
    /// its assembly in the output directory).
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
        // Honua.Ai.Tests carries the MCP protocol-surface tests (MCP lives in
        // Honua.Ai, not a standalone Honua.Protocols.Mcp), so it holds endpoint /
        // operation coverage that the scans must see alongside the Server.Tests
        // and per-protocol Honua.Protocols.*.Tests assemblies.
        var patterns = new[]
        {
            "Honua.Server.Tests.dll",
            "Honua.Protocols.*.Tests.dll",
            "Honua.Ai.Tests.dll",
            "Honua.Worker.Gdal.Tests.dll"
        };

        var assemblies = new List<Assembly>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(baseDir, pattern))
            {
                if (!seen.Add(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    assemblies.Add(Assembly.LoadFrom(path));
                }
                catch (BadImageFormatException)
                {
                    // Native / mixed-mode sidecar that happens to match the glob — skip.
                }
            }
        }

        return assemblies;
    }

    /// <summary>
    /// Returns true when the method or its declaring test class carries one of
    /// the test attributes that emits <c>Category=Integration</c>.
    /// </summary>
    internal static bool IsIntegrationTestMethod(Type type, MethodInfo method)
        => HasIntegrationAttribute(type) || HasIntegrationAttribute(method);

    /// <summary>
    /// Returns true when the type or any of its test methods carries one of the
    /// test attributes that emits <c>Category=Integration</c>.
    /// </summary>
    internal static bool IsIntegrationTestClass(Type type)
        => HasIntegrationAttribute(type) ||
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
                foreach (var method in type.GetMethods(TestMemberFlags))
                {
                    if (IsIntegrationTestMethod(type, method))
                    {
                        yield return method;
                    }
                }
            }
        }
    }

    private static bool HasIntegrationAttribute(MemberInfo member)
        => member.CustomAttributes.Any(attribute =>
            IntegrationAttributeTypes.Any(type => attribute.AttributeType == type));

    /// <summary>
    /// Resolves the repository root by walking upward until Honua.sln is found.
    /// </summary>
    internal static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("Unable to locate repository root.");
        }

        return directory.FullName;
    }

    /// <summary>
    /// Returns the bare project names (filename without extension) of every direct
    /// <c>&lt;ProjectReference&gt;</c> declared in the given csproj. Blank includes are
    /// skipped and Windows path separators are normalized so the result is stable
    /// across platforms.
    /// </summary>
    internal static IReadOnlyList<string> DirectProjectReferenceNames(string csprojPath)
        => DirectReferenceValues(csprojPath, "ProjectReference")
            .Select(value => Path.GetFileNameWithoutExtension(value.Replace('\\', '/'))!)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

    /// <summary>
    /// Returns the raw <c>Include</c> values of every direct
    /// <c>&lt;PackageReference&gt;</c> declared in the given csproj. Blank includes are
    /// skipped.
    /// </summary>
    internal static IReadOnlyList<string> DirectPackageReferenceNames(string csprojPath)
        => DirectReferenceValues(csprojPath, "PackageReference").ToList();

    private static IEnumerable<string> DirectReferenceValues(string csprojPath, string elementName)
        => XDocument.Load(csprojPath)
            .Descendants(elementName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
}
