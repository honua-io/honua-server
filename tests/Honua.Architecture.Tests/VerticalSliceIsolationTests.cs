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
/// Reference: CLAUDE.md Architecture Enforcement
/// </summary>
[Trait("Category", "Architecture")]
public sealed class VerticalSliceIsolationTests
{
    private static readonly string[] _featureNames =
    {
        "Admin",
        "FeatureServer",
        "Ogc",
        "OgcFeatures",
        "OgcTiles",
        "Tiles",
        "OData",
        "Import",
        "FileStorage",
        "HealthCheck",
        "Infrastructure" // Infrastructure is allowed to be referenced by others
    };

    [ArchitectureTest]
    public void Features_ShouldNotDirectlyReference_OtherFeatures()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;
        var violations = new List<string>();

        foreach (var featureName in _featureNames.Where(f => f is not ("Infrastructure" or "Ogc")))
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

            // Skip infrastructure as it's a cross-cutting concern
            if (featureDir.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                featureDir.Equals("Ogc", StringComparison.OrdinalIgnoreCase))
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

            hasEndpointsOrHandler.Should().BeTrue(
                $"Feature '{featureDir}' should follow vertical slice pattern with endpoints and/or handlers. " +
                $"Expected to find files like {string.Join(", ", expectedFiles)} in {featurePath}");
        }
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

    private static List<string> FindCrossFeatureReferences(Type[] featureTypes, string currentFeature)
    {
        var violations = new List<string>();
        var otherFeatures = _featureNames.Where(f => f != currentFeature && f is not ("Infrastructure" or "Ogc")).ToArray();

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
