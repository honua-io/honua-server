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
        "AnalysisContent",
        "AiBuilder",
        "Alerts",
        "Capabilities",
        "CloudDemo",
        "Collaboration",
        "Console",
        "Forms",
        "Geocoding",
        "Geoprocessing",
        "Grounding",
        "Orchestration",
        "PackageReview",
        "Protocols",
        "Mobile", // Parent container for mobile sub-feature slices (e.g. FieldCollection sync)
        "NlQuery",
        "Export",
        "Import",
        "FileImport",   // Import-split: file-format ingest slice.
        "Migration",    // Import-split: ArcGIS/legacy migration slice.
        "RasterImport", // Import-split: raster ingest slice.
        "ControlPlane", // Relocated out of the Infrastructure grab-bag (audit-A1).
        "FileStorage",
        "PrintingTools",
        "HealthCheck",
        "Streaming",
        "Infrastructure", // Infrastructure is allowed to be referenced by others
        "StaticMap",
        "SpatialAnalytics",
        "Spec",
        "Reporting",
        "Studio",
        "Styling", // Relocated out of the Infrastructure grab-bag into its own top-level slice.
        "WorkflowPackages",
        "Temporal", // Temporal data history slice (honua-server#1166): capability discovery + as-of read.
        "FieldWorkflows" // Back-office field workflow slice (honua-server#1158/#1159/#1160): review/QA + exports over form submissions.
    };

    /// <summary>
    /// Sub-areas under <c>Honua.Infrastructure.*</c> that are explicitly
    /// permitted shared plumbing. The arch test treats these as a closed allow-list so
    /// future additions to Infrastructure require an intentional update here. See ADR-0044
    /// for the carve-out that moved Authentication / Caching / Events / Helpers / Models /
    /// Validation out of Honua.Infrastructure into Honua.Hosting (their
    /// namespaces are preserved under <c>Honua.Infrastructure.*</c> but
    /// the source lives in the Hosting assembly).
    /// </summary>
    /// <remarks>
    /// Audit-A1 (structural-audit-2026-05): the audit found Infrastructure had grown to
    /// 351 files / 42 subdirs with Auth(45), ControlPlane(38), Styling(26), Rendering(16)
    /// misfiled — entries that should have been their own slices. The Hosting carve moved
    /// six sub-areas out (Authentication, Caching, Events, Helpers, Models, Validation),
    /// and ControlPlane / Styling have been extracted to their own slices. Anything not on
    /// this allow-list is treated as a real feature slice and must comply with vertical
    /// isolation. Update the list when a new genuinely-shared subsystem is added; reject
    /// additions that look like a slice in disguise.
    /// </remarks>
    private static readonly HashSet<string> _infrastructureAllowedSubAreas =
        new(StringComparer.Ordinal)
        {
            // Genuinely shared plumbing.
            "Middleware",
            "Monitoring",
            "Services",
            "Abstractions",
            "Extensions",
            "Infrastructure",   // Honua.Hosting/Features/Infrastructure holds the shared
                                // Honua.Infrastructure.* base types (e.g.
                                // IConfigurationDocumentationContributor, job-cancellation
                                // notifier extensions) lifted in the Hosting carve.
            "Alerts",           // Hosting-resident shared base of the Alerts slice (delivery
                                // options/metadata/sink contracts); the Alerts endpoints stay
                                // in Honua.Server/Features/Alerts.
            "FileStorage",      // Hosting-resident cloud-storage base types + stream wrappers
                                // (CloudFileStorageBase, progress/cancellation streams) shared
                                // by the storage providers; the FileStorage endpoints +
                                // LocalFileStorage stay in Honua.Server/Features/FileStorage.
            "PackageReview",    // Hosting-resident PackageReviewContextFactory: the HttpContext->
                                // PackageReviewContext helper shared by the Server PackageReview
                                // endpoints and the MCP package-review tool (the AI surface), so
                                // the latter doesn't couple to the former's endpoint class.
            "Helpers",          // Hosting carve preserves Honua.Infrastructure.Helpers.*
            // Cross-cutting subsystems.
            "Analytics",
            "AuditLog",
            "Authentication",   // Lives in Honua.Hosting under the preserved namespace.
            "Caching",          // Lives in Honua.Hosting under the preserved namespace.
            "Compression",
            "Configuration",
            "Coordination",
            "DataIntegrity",
            "Events",           // Bulk lives in Honua.Hosting; FeatureChangeRetryQueue remains in Server.
            "Filtering",
            "GeoJson",
            "Geometries",
            "HealthCheck",
            "HealthChecks",
            "Hosting",
            "Http",
            "Licensing",
            "Logging",
            "Models",           // Lives in Honua.Hosting under the preserved namespace.
            "MultiTenancy",
            "Parsing",
            "Progress",
            "Raster",
            "RateLimiting",
            "Redis",
            "Rendering",
            "Resilience",
            "Scene",
            "Security",
            "Validation",       // Lives in Honua.Hosting under the preserved namespace.
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
            // Capabilities aggregates a service-metadata manifest across slices
            // (ingest registry, streaming descriptors, ControlPlane options).
            ["Capabilities"] = new[] { "Import", "Streaming", "ControlPlane", "FileImport", "Migration", "RasterImport" },
            // Admin is the composition/administration surface: its endpoints adapt
            // several domain slices directly (Styling SLD CRUD, the ControlPlane
            // deploy-workflow service, the ingest pipeline, Console content).
            // Surfaced once these became tracked features; tech debt to retire by
            // routing through shared Core abstractions (and, for the ingest set,
            // by the Honua.Import extraction).
            ["Admin"] = new[] { "Styling", "ControlPlane", "Console", "Import", "Migration", "FileImport", "RasterImport" },
            // ControlPlane orchestrates deploy/release workflows over the
            // Geoprocessing job runtime and is wired from the Admin surface.
            ["ControlPlane"] = new[] { "Geoprocessing", "Admin" },
            // Studio composes the Console authoring surface.
            ["Studio"] = new[] { "Console" },
            // The ingest cluster (Import + Migration + FileImport + RasterImport)
            // is one cohesive unit destined for the Honua.Import module; intra-
            // cluster references are expected and move out of Server together.
            ["Import"] = new[] { "Migration", "FileImport", "RasterImport" },
            ["Migration"] = new[] { "Import", "FileImport", "RasterImport" },
            ["FileImport"] = new[] { "Import", "Migration", "RasterImport" },
            ["RasterImport"] = new[] { "Import", "Migration", "FileImport" },
            ["Mcp"] = new[] { "Geoprocessing", "Grounding", "Reporting" },
            ["Grounding"] = new[] { "Geoprocessing" },
            ["AnalysisContent"] = new[] { "Geoprocessing" },
            ["NlQuery"] = new[] { "AiBuilder" },
            ["PackageReview"] = new[] { "Geoprocessing" },
            ["Reporting"] = new[] { "Geoprocessing" },
            ["WorkflowPackages"] = new[] { "Geoprocessing", "Orchestration" }
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

    /// <summary>
    /// Audit-A1 ratchet: confirms the Infrastructure carve-out is intact. The blanket
    /// "Infrastructure is exempt" rule is anchored to the explicit
    /// <see cref="_infrastructureAllowedSubAreas"/> allow-list — any new top-level folder
    /// under <c>src/Honua.Server/Features/Infrastructure/</c> must be reviewed and either
    /// (a) extracted to its own slice (preferred for misfiled slices like the historical
    /// Auth/ControlPlane/Styling sub-areas) or (b) added to the allow-list with a comment
    /// explaining why it is shared plumbing.
    /// </summary>
    /// <remarks>
    /// See <see cref="_infrastructureAllowedSubAreas"/> for the historical context and
    /// ADR-0044 for the Hosting carve. This test scans the filesystem (not the assembly)
    /// so it captures namespaces whose source has moved to Honua.Hosting under the same
    /// <c>Honua.Infrastructure.*</c> namespace.
    /// </remarks>
    [ArchitectureTest]
    public void Infrastructure_SubAreas_ShouldBeOnExplicitAllowList()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var projectRoot = FindProjectRoot(currentDir);
        var infraServerPath = Path.Combine(projectRoot, "src", "Honua.Server", "Features", "Infrastructure");
        var infraHostingPath = Path.Combine(projectRoot, "src", "Honua.Hosting", "Features");

        var observed = new HashSet<string>(StringComparer.Ordinal);
        AccumulateSubAreaNames(infraServerPath, observed);
        AccumulateSubAreaNames(infraHostingPath, observed);

        var stowaways = observed
            .Where(name => !_infrastructureAllowedSubAreas.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        stowaways.Should().BeEmpty(
            "Every top-level sub-directory under Honua.Infrastructure (or " +
            "Honua.Hosting/Features) must be on the audit-A1 allow-list. Add the sub-area " +
            "to VerticalSliceIsolationTests._infrastructureAllowedSubAreas with a justifying " +
            "comment, or extract it to its own vertical slice. Unexpected sub-areas: " +
            string.Join(", ", stowaways));

        static void AccumulateSubAreaNames(string path, HashSet<string> sink)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(path))
            {
                sink.Add(new DirectoryInfo(dir).Name);
            }
        }
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
            "Honua.Protocols.GeoServices.GPServer"
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

        // Allow Infrastructure sub-modules (e.g., Honua.Infrastructure.Authentication)
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
