// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces vertical slice isolation by preventing cross-feature dependencies.
/// Each feature should be independent and not directly reference other features.
/// Reference: AGENTS.md Architecture Enforcement
/// </summary>
[Trait("Category", "Architecture")]
public sealed class VerticalSliceIsolationTests
{
    private static readonly string[] _featureNames =
    {
        "Admin",
        "AiBuilder",
        "Alerts",
        "CloudDemo",
        "Collaboration",
        "Geocoding",
        "Geoprocessing",
        "Grounding",
        "Orchestration",
        "Protocols",
        "Mobile", // Parent container for mobile sub-feature slices (e.g. FieldCollection sync)
        "NlQuery",
        "Export",
        "Import",
        "FileStorage",
        "PrintingTools",
        "HealthCheck",
        "Streaming",
        "Infrastructure", // Infrastructure is allowed to be referenced by others
        "StaticMap",
        "SpatialAnalytics",
        "Spec",
        "Reporting"
    };

    /// <summary>
    /// Protocol-adapter features that are allowed to consume a specific domain
    /// feature's transport-neutral services. The Mcp operator surface adapts
    /// <c>IGeoprocessingJobService</c> and <c>IAnalysisReportService</c>, but
    /// lives in its own vertical slice per ticket #728 / #801.
    /// Grounding is a domain feature that reuses the Geoprocessing process
    /// catalog + destructive classifier per ticket #742. Each entry lists the
    /// cross-feature references permitted for the key.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyCollection<string>> _allowedCrossFeatureRefs =
        new(StringComparer.Ordinal)
        {
            ["Mcp"] = new[] { "Geoprocessing", "Grounding", "Reporting" },
            ["Grounding"] = new[] { "Geoprocessing" },
            ["NlQuery"] = new[] { "AiBuilder" },
            ["Reporting"] = new[] { "Geoprocessing" }
        };

    private static readonly string[] _protocolAdapterNames =
    {
        "Cog",
        "Grpc",
        "Mcp"
    };

    [ArchitectureTest]
    public void Features_ShouldNotDirectlyReference_OtherFeatures()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;
        var violations = new List<string>();

        foreach (var featureName in _featureNames.Where(f => f is not ("Infrastructure" or "Protocols")))
        {
            var featureTypes = GetTypesInFeature(serverAssembly, featureName);
            var illegalReferences = FindCrossFeatureReferences(featureTypes, featureName);
            violations.AddRange(illegalReferences);
        }

        violations.Should().BeEmpty(
            "Features must maintain vertical slice isolation and not directly reference other features. " +
            "Cross-feature communication should happen through shared abstractions in Core or Infrastructure layers. " +
            "Infrastructure is an exception as it provides shared services.");
    }

    [ArchitectureTest]
    public void FeatureDirectories_ShouldFollowVerticalSlicePattern()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var projectRoot = FindProjectRoot(currentDir);
        var featuresPath = Path.Combine(projectRoot, "src", "Honua.Server", "Features");

        Directory.Exists(featuresPath).Should().BeTrue($"Features directory should exist at {featuresPath}");

        var featureDirectories = Directory.GetDirectories(featuresPath)
            .Select(dir => new DirectoryInfo(dir).Name)
            .ToArray();

        // Verify each feature has proper structure
        foreach (var featureDir in featureDirectories)
        {
            var featurePath = Path.Combine(featuresPath, featureDir);

            // Skip infrastructure as it's a cross-cutting concern.
            // Skip parent containers (Protocols, Mobile) whose vertical slices live in sub-directories.
            if (featureDir.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                featureDir.Equals("Protocols", StringComparison.OrdinalIgnoreCase) ||
                featureDir.Equals("Mobile", StringComparison.OrdinalIgnoreCase))
                continue;

            // Each feature should have at least one of these files to be considered a proper vertical slice
            var expectedFiles = new[]
            {
                $"{featureDir}Endpoints.cs",
                "Endpoints.cs",
                $"{featureDir}Handler.cs",
                "Handler.cs"
            };

            var hasEndpointsOrHandler = expectedFiles
                .Select(file => Path.Combine(featurePath, file))
                .Any(File.Exists) ||
                Directory.GetFiles(featurePath, "*Endpoints.cs").Length > 0 ||
                Directory.GetFiles(featurePath, "*Handler.cs").Length > 0;

            if (!hasEndpointsOrHandler)
            {
                hasEndpointsOrHandler =
                    Directory.GetFiles(featurePath, "*ServiceCollectionExtensions.cs").Length > 0 ||
                    Directory.GetFiles(featurePath, "*BackgroundService.cs").Length > 0;
            }

            hasEndpointsOrHandler.Should().BeTrue(
                $"Feature '{featureDir}' should follow vertical slice pattern with endpoints and/or handlers. " +
                $"Expected to find files like {string.Join(", ", expectedFiles)} in {featurePath}");
        }
    }

    [ArchitectureTest]
    public void Mcp_ShouldNotReference_OtherProtocolAdapters()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;
        var mcpTypes = GetTypesInProtocol(serverAssembly, "Mcp");

        var forbiddenNamespaces = new[]
        {
            "Honua.Server.Features.Protocols.Grpc",
            "Honua.Server.Features.Protocols.GeoServices.GPServer"
        };

        var violations = new List<string>();
        foreach (var type in mcpTypes)
        {
            foreach (var referenced in CollectReferencedTypes(type))
            {
                var ns = referenced.Namespace;
                if (string.IsNullOrEmpty(ns))
                {
                    continue;
                }

                foreach (var forbidden in forbiddenNamespaces)
                {
                    if (ns.Equals(forbidden, StringComparison.Ordinal) ||
                        ns.StartsWith(forbidden + ".", StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"Mcp type '{type.FullName}' references '{referenced.FullName}' from '{forbidden}'. " +
                            $"The MCP operator surface must only consume canonical domain services, not other protocol adapters.");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "The MCP feature must only adapt transport-neutral domain services (Geoprocessing), " +
            "not other protocol adapters like Grpc or the GPServer REST endpoints.");
    }

    [ArchitectureTest]
    public void ProtocolAdapters_ShouldLiveUnder_ProtocolsFolder()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var projectRoot = FindProjectRoot(currentDir);
        var featuresPath = Path.Combine(projectRoot, "src", "Honua.Server", "Features");

        foreach (var adapterName in _protocolAdapterNames)
        {
            Directory.Exists(Path.Combine(featuresPath, adapterName)).Should().BeFalse(
                $"{adapterName} is a protocol adapter and must live under Features/Protocols/{adapterName}");
        }

        var legacyCogFolder = string.Concat("Cloud", "Cog");
        Directory.Exists(Path.Combine(featuresPath, legacyCogFolder)).Should().BeFalse(
            "COG protocol code should use the concise Features/Protocols/Cog location and naming.");
    }

    [ArchitectureTest]
    public void FeatureNamespaces_ShouldMatchDirectoryStructure()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;
        var violations = new List<string>();

        var typesInFeatures = serverAssembly.GetTypes()
            .Where(type => type.Namespace?.Contains("Features") == true)
            .Where(type => !string.IsNullOrEmpty(type.Namespace))
            .ToArray();

        foreach (var type in typesInFeatures)
        {
            var namespaceParts = type.Namespace!.Split('.');
            var featuresIndex = Array.IndexOf(namespaceParts, "Features");

            if (featuresIndex >= 0 && featuresIndex + 1 < namespaceParts.Length)
            {
                var featureName = namespaceParts[featuresIndex + 1];

                // Check if the feature name matches our expected feature list or follows valid patterns
                if (!IsValidFeatureName(featureName))
                {
                    violations.Add($"Type {type.FullName} is in feature namespace '{featureName}' " +
                                 $"which doesn't match expected feature naming patterns. " +
                                 $"Expected one of: {string.Join(", ", _featureNames)}");
                }
            }
        }

        violations.Should().BeEmpty(
            "Feature namespaces should match the directory structure and follow consistent naming patterns.");
    }

    private static Type[] GetTypesInFeature(Assembly assembly, string featureName)
    {
        return assembly.GetTypes()
            .Where(type => type.Namespace?.Contains($"Features.{featureName}") == true)
            .ToArray();
    }

    private static Type[] GetTypesInProtocol(Assembly assembly, string protocolName)
    {
        return assembly.GetTypes()
            .Where(type => type.Namespace?.Contains($"Features.Protocols.{protocolName}") == true)
            .ToArray();
    }

    private static List<string> FindCrossFeatureReferences(Type[] featureTypes, string currentFeature)
    {
        var violations = new List<string>();
        var allowed = _allowedCrossFeatureRefs.TryGetValue(currentFeature, out var permitted)
            ? permitted
            : Array.Empty<string>();
        var otherFeatures = _featureNames
            .Where(f => f != currentFeature
                && f is not ("Infrastructure" or "Protocols")
                && !allowed.Contains(f, StringComparer.Ordinal))
            .ToArray();

        foreach (var type in featureTypes)
        {
            // Check field types for cross-feature references
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (var field in fields)
            {
                var violation = CheckTypeForCrossFeatureReference(field.FieldType, type, currentFeature, otherFeatures);
                if (violation != null)
                    violations.Add(violation);
            }

            // Check property types
            var properties = type.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (var property in properties)
            {
                var violation = CheckTypeForCrossFeatureReference(property.PropertyType, type, currentFeature, otherFeatures);
                if (violation != null)
                    violations.Add(violation);
            }

            // Check constructor parameters
            var constructors = type.GetConstructors();
            foreach (var constructor in constructors)
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    var violation = CheckTypeForCrossFeatureReference(parameter.ParameterType, type, currentFeature, otherFeatures);
                    if (violation != null)
                        violations.Add(violation);
                }
            }

            // Check method parameters and return types
            var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                // Check return type
                var returnViolation = CheckTypeForCrossFeatureReference(method.ReturnType, type, currentFeature, otherFeatures);
                if (returnViolation != null)
                    violations.Add(returnViolation);

                // Check parameters
                foreach (var parameter in method.GetParameters())
                {
                    var violation = CheckTypeForCrossFeatureReference(parameter.ParameterType, type, currentFeature, otherFeatures);
                    if (violation != null)
                        violations.Add(violation);
                }
            }
        }

        return violations;
    }

    private static string? CheckTypeForCrossFeatureReference(Type typeToCheck, Type containingType, string currentFeature, string[] otherFeatures)
    {
        // Handle generic types
        if (typeToCheck.IsGenericType)
        {
            var genericArgs = typeToCheck.GetGenericArguments();
            foreach (var arg in genericArgs)
            {
                var violation = CheckTypeForCrossFeatureReference(arg, containingType, currentFeature, otherFeatures);
                if (violation != null)
                    return violation;
            }
        }

        // Check if the type belongs to another feature
        var typeNamespace = typeToCheck.Namespace;
        if (string.IsNullOrEmpty(typeNamespace))
            return null;

        if (!typeNamespace.StartsWith("Honua.Server.Features.", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var otherFeature in otherFeatures)
        {
            if (typeNamespace.Contains($"Features.{otherFeature}"))
            {
                return $"Feature '{currentFeature}' type '{containingType.FullName}' " +
                       $"directly references type '{typeToCheck.FullName}' from feature '{otherFeature}'. " +
                       $"Use shared abstractions in Core or Infrastructure instead.";
            }
        }

        return null;
    }

    private static bool IsValidFeatureName(string featureName)
    {
        // Allow exact matches with our feature list
        if (_featureNames.Contains(featureName, StringComparer.OrdinalIgnoreCase))
            return true;

        // Allow Infrastructure sub-modules (e.g., Infrastructure.Authentication)
        if (featureName.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static IEnumerable<Type> CollectReferencedTypes(Type type)
    {
        var seen = new HashSet<Type>();

        foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var t in Expand(field.FieldType))
            {
                if (seen.Add(t)) yield return t;
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var t in Expand(property.PropertyType))
            {
                if (seen.Add(t)) yield return t;
            }
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var t in Expand(parameter.ParameterType))
                {
                    if (seen.Add(t)) yield return t;
                }
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (var t in Expand(method.ReturnType))
            {
                if (seen.Add(t)) yield return t;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var t in Expand(parameter.ParameterType))
                {
                    if (seen.Add(t)) yield return t;
                }
            }
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var inner in Expand(arg))
                {
                    yield return inner;
                }
            }
        }
    }

    private static string FindProjectRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException($"Could not find project root starting from {startPath}");
    }
}
