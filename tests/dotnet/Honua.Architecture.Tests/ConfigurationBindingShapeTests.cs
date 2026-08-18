// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Protects source-generated configuration binding from init-only properties (#3055).
/// </summary>
/// <remarks>
/// <para>
/// Covered registration shapes: <c>Configure&lt;T&gt;(IConfiguration)</c>,
/// <c>AddOptions&lt;T&gt;().Bind(...)</c> / <c>.BindConfiguration(...)</c> (chained or through a
/// deferred builder local), <c>Get&lt;T&gt;()</c>, <c>GetSection(T.SectionName).Bind(instance)</c>, and
/// <c>section.Bind(local)</c> where the local is created with <c>new T()</c>. All of these use the
/// source-generated assignment path, which silently skips init-only properties.
/// </para>
/// <para>
/// A type that is populated by a hand-rolled parser instead of a binder is NOT in scope here — it is
/// unreachable from any binding root by construction. That shape is guarded by
/// <see cref="HandParsedConfigurationSectionTests"/> (#3315).
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed partial class ConfigurationBindingShapeTests
{
    /// <summary>
    /// Drift anchor, not the discovery mechanism: roots are discovered from source on every run;
    /// these known registrations must keep being discovered or the extraction patterns have
    /// rotted. The tail entries anchor one representative per non-generic registration shape
    /// (direct instance binds via <c>new T()</c> locals and <c>T.SectionName</c> section hints).
    /// </summary>
    private static readonly string[] AuditedRootNames =
    [
        "AlertDeliveryOptions",
        "AlertOptions",
        "AuditChainVerificationOptions",
        "AuditExportOptions",
        "DeploymentOptions",
        "FederationSourceOptions",
        "FieldCollectionAutomationOptions",
        "LimitsOptions",
        "MigrationSafetyOptions",
        "QueryCacheOptions",
        "SceneDatasetOptions",
        "SecureConfigurationOptions",
        "SpecCostEstimatorOptions",
        "StartupResilienceOptions",
        "TemporaryFileOptions",
        "TileOptions",
        "WorkspaceOptions",
        // Direct instance-bind shapes (#3055 follow-up): `var options = new T(); section.Bind(options)`
        // and `GetSection(T.SectionName).Bind(instance)`.
        "MySqlOptions",
        "OidcAuthenticationOptions",
        "OutputCacheTtlOptions",
        "SecurityHeadersOptions",
    ];

    /// <summary>
    /// Nested option graphs that the recursive walk must keep reaching (#3306). A root being
    /// discovered is not enough — the walk has to descend through its nested option objects, which
    /// is where the init-only properties actually hide. Each entry is a qualified type name that is
    /// only reachable through another type's property, so if the traversal ever stops descending the
    /// guard fails loudly instead of going quietly green over a smaller graph.
    /// </summary>
    private static readonly string[] AuditedNestedGraphNames =
    [
        // Limits -> Imports. #3306 was filed believing this reached
        // Honua.Core.Features.Import.Domain.ImportLimits; it does not, and must not — that is a
        // DIFFERENT, identically-named type built by a hand parser and guarded by
        // HandParsedConfigurationSectionTests. LimitsOptions.Imports is the settable
        // Honua.Core.Configuration.ImportLimits declared in AdvancedLimits.cs, and the walk must
        // keep descending into it.
        "Honua.Core.Configuration.ImportLimits",
        "Honua.Core.Configuration.AttachmentLimits",
        "Honua.Core.Configuration.TileLimits",
        "Honua.Core.Configuration.ConnectionLimits",
        "Honua.Core.Configuration.AnalyticsLimits",
        "Honua.Core.Configuration.ElevationLimits",
        "Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderConfiguration",
    ];

    /// <summary>
    /// Recorded decisions for init-only types that are deliberately NOT converted to setters
    /// (#3306 AC4). Each entry asserts the type stays OUT of the bound graph: it is a domain or
    /// metadata record, not an options root, so <c>init</c> is correct. If a future change makes one
    /// of these reachable from a binding root, this guard fails and forces the decision to be
    /// re-taken — either the registration is wrong, or the record must move to ordinary setters.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyUnboundInitOnlyTypes =
        new(StringComparer.Ordinal)
        {
            ["Honua.Core.Features.Security.Domain.AccessPolicy"] =
                "Appears only as a nested property of domain/metadata records (SceneDataset, " +
                "MetadataV2Graph, ContentPublicationPolicy), never of an options root. It is " +
                "deserialized from metadata JSON, not bound from IConfiguration, so init-only is " +
                "correct and PR #3113's conversion was unnecessary (#3306).",
            ["Honua.Core.Features.Import.Domain.ImportLimits"] =
                "Built by the hand-rolled Import:Limits parser in " +
                "src/Honua.Db/Postgres/ServiceCollectionExtensions.cs, not by the options binder, " +
                "so init-only is safe here. Key coverage for that parser is guarded by " +
                "HandParsedConfigurationSectionTests (#3315).",
        };

    [ArchitectureTest]
    public void ConfigureBoundOptionGraphs_MustNotDeclareInitOnlyProperties()
    {
        var sources = ConfigurationSourceModel.LoadSourceFiles();

        var typesByName = ConfigurationSourceModel.ParseTypes(sources);
        var boundRootNames = DiscoverConfigurationBoundRoots(sources.Values, typesByName);
        var boundRootSimpleNames = boundRootNames
            .Select(ConfigurationSourceModel.SimpleTypeName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var auditedRootName in AuditedRootNames)
        {
            Assert.Contains(auditedRootName, boundRootSimpleNames);
        }

        // Collision anchor: the Postgres registration imports the configuration type;
        // the unrelated per-request cache model must not enter the mutable binding graph.
        Assert.Contains("Honua.Core.Features.Infrastructure.Domain.QueryCacheOptions", boundRootNames);
        Assert.DoesNotContain("Honua.Infrastructure.Caching.QueryCacheOptions", boundRootNames);

        var pending = new Stack<ConfigurationSourceModel.TypeShape>(
            typesByName.Values
                .SelectMany(static shapes => shapes)
                .Where(shape => boundRootNames.Contains(shape.QualifiedName)));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        // #3306: a property whose simple type name matches several declarations that neither the
        // declaring namespace nor the file's imports disambiguate used to be dropped silently, so
        // the walk could stop early and stay green over a graph it never actually inspected. Such a
        // reference is now collected and reported rather than skipped.
        var unresolvable = new SortedSet<string>(StringComparer.Ordinal);

        while (pending.TryPop(out var typeShape))
        {
            if (!visited.Add(typeShape.QualifiedName))
            {
                continue;
            }

            foreach (var property in typeShape.Properties)
            {
                if (property.IsInitOnly)
                {
                    violations.Add($"{typeShape.Path}: {typeShape.QualifiedName}.{property.Name}");
                }

                foreach (var candidateName in ConfigurationSourceModel
                             .QualifiedTypeIdentifierPattern()
                             .Matches(property.TypeName)
                             .Select(match => match.Value)
                             .Distinct(StringComparer.Ordinal))
                {
                    if (!typesByName.ContainsKey(
                            ConfigurationSourceModel.SimpleTypeName(candidateName)))
                    {
                        continue;
                    }

                    var referencedTypes = ConfigurationSourceModel.ResolveDeclaredTypes(
                        candidateName,
                        typeShape.Namespace,
                        typeShape.UsingNamespaces,
                        typesByName,
                        out var ambiguous);
                    if (ambiguous)
                    {
                        unresolvable.Add(
                            $"{typeShape.QualifiedName}.{property.Name} -> {candidateName}");
                        continue;
                    }

                    foreach (var referencedType in referencedTypes)
                    {
                        pending.Push(referencedType);
                    }
                }
            }

            foreach (var baseTypeName in typeShape.BaseTypeNames)
            {
                var baseTypes = ConfigurationSourceModel.ResolveDeclaredTypes(
                    baseTypeName,
                    typeShape.Namespace,
                    typeShape.UsingNamespaces,
                    typesByName,
                    out var ambiguousBase);
                if (ambiguousBase)
                {
                    unresolvable.Add($"{typeShape.QualifiedName} : {baseTypeName}");
                    continue;
                }

                foreach (var baseType in baseTypes)
                {
                    pending.Push(baseType);
                }
            }
        }

        foreach (var nestedGraphName in AuditedNestedGraphNames)
        {
            Assert.Contains(nestedGraphName, visited);
        }

        foreach (var (typeName, reason) in DeliberatelyUnboundInitOnlyTypes)
        {
            Assert.False(
                visited.Contains(typeName),
                $"{typeName} is now reachable from a configuration binding root, but it was recorded " +
                $"as deliberately init-only: {reason} Either the new registration is wrong, or the " +
                "type must move to ordinary setters and lose its entry here.");
        }

        Assert.True(
            unresolvable.Count == 0,
            "The reachability walk could not resolve these property/base types, so part of a bound " +
            "option graph was never inspected (#3306). Qualify the type name at the declaration " +
            "site so the walk can follow it: " + string.Join(", ", unresolvable));

        Assert.True(
            violations.Count == 0,
            "Types bound through Configure<T>(IConfiguration), AddOptions<T>().Bind, or Get<T>() " +
            "must use " +
            "ordinary setters throughout their reachable object graph. Init-only properties: " +
            string.Join(", ", violations));
    }

    private static HashSet<string> DiscoverConfigurationBoundRoots(
        IEnumerable<string> sources,
        IReadOnlyDictionary<string, List<ConfigurationSourceModel.TypeShape>> typesByName)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.Select(ConfigurationSourceModel.StripCommentsAndLiterals))
        {
            var namespaceName = ConfigurationSourceModel.MatchNamespace(source);
            var usingNamespaces = ConfigurationSourceModel.MatchUsingNamespaces(source);
            var candidateTypeNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in ChainedAddOptionsPattern()
                         .Matches(source)
                         .Where(match => ContainsConfigurationBind(match.Groups["tail"].Value)))
            {
                candidateTypeNames.Add(match.Groups["type"].Value);
            }

            var boundVariables = VariableBindPattern()
                .Matches(source)
                .Select(match => match.Groups["variable"].Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (Match match in AddOptionsVariablePattern()
                         .Matches(source)
                         .Where(match => boundVariables.Contains(match.Groups["variable"].Value)))
            {
                candidateTypeNames.Add(match.Groups["type"].Value);
            }

            foreach (var typeName in ConfigurePattern()
                         .Matches(source)
                         .Select(match => new
                         {
                             Name = match.Groups["type"].Value,
                             Tail = match.Groups["tail"].Value
                         })
                         .Where(candidate =>
                             !string.Equals(
                                 ConfigurationSourceModel.SimpleTypeName(candidate.Name),
                                 "TOptions",
                                 StringComparison.Ordinal) &&
                             ContainsConfigurationBind(candidate.Tail))
                         .Select(candidate => candidate.Name))
            {
                candidateTypeNames.Add(typeName);
            }

            // IConfiguration.Get<T>() creates an instance and then uses the same generated
            // binding path, so init-only accessors are unsafe here too.
            candidateTypeNames.UnionWith(GetPattern()
                .Matches(source)
                .Select(match => match.Groups["type"].Value));

            // GetSection(T.SectionName).Bind(instance): the section expression names the bound
            // type directly. This shape appears both standalone and inside Configure<T>(options
            // => ...) lambdas, whose delegate overload the generic scan above ignores.
            candidateTypeNames.UnionWith(SectionNameBindPattern()
                .Matches(source)
                .Select(match => match.Groups["type"].Value));

            // section.Bind(local) where the local was created with `new T()` (for example the
            // provider options in AddMySqlServices). Recover T from the local's declaration.
            var newInstanceTypesByVariable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (Match match in NewInstanceDeclarationPattern().Matches(source))
            {
                var variable = match.Groups["variable"].Value;
                if (!newInstanceTypesByVariable.TryGetValue(variable, out var typeNames))
                {
                    typeNames = new HashSet<string>(StringComparer.Ordinal);
                    newInstanceTypesByVariable.Add(variable, typeNames);
                }

                typeNames.Add(match.Groups["type"].Value);
            }

            candidateTypeNames.UnionWith(BindArgumentPattern()
                .Matches(source)
                .Select(match => match.Groups["argument"].Value)
                .Where(newInstanceTypesByVariable.ContainsKey)
                .SelectMany(argument => newInstanceTypesByVariable[argument]));

            roots.UnionWith(candidateTypeNames.SelectMany(typeName =>
                ConfigurationSourceModel.ResolveDeclaredTypes(
                    typeName,
                    namespaceName,
                    usingNamespaces,
                    typesByName))
                .Select(shape => shape.QualifiedName));
        }

        return roots;
    }

    private static bool ContainsConfigurationBind(string invocationTail)
    {
        var trimmed = invocationTail.Trim();
        return invocationTail.Contains(".Bind(", StringComparison.Ordinal) ||
               invocationTail.Contains(".BindConfiguration(", StringComparison.Ordinal) ||
               invocationTail.Contains("GetSection(", StringComparison.Ordinal) ||
               string.Equals(trimmed, "section", StringComparison.Ordinal) ||
               string.Equals(trimmed, "localSection", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"AddOptions\s*<\s*(?<type>[\w.]+)\s*>\s*\(\s*\)(?<tail>.{0,1200}?)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex ChainedAddOptionsPattern();

    [GeneratedRegex(
        @"(?:var|[\w.<>]+)\s+(?<variable>\w+)\s*=\s*[^;]*?AddOptions\s*<\s*(?<type>[\w.]+)\s*>\s*\(\s*\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex AddOptionsVariablePattern();

    [GeneratedRegex(@"\b(?<variable>\w+)\s*\.\s*Bind(?:Configuration)?\s*\(")]
    private static partial Regex VariableBindPattern();

    [GeneratedRegex(
        @"GetSection\s*\(\s*(?<type>[\w.]+)\s*\.\s*SectionName\s*\)\s*\.\s*Bind\s*\(")]
    private static partial Regex SectionNameBindPattern();

    [GeneratedRegex(
        @"(?:var|[\w.<>]+)\s+(?<variable>\w+)\s*=\s*new\s+(?<type>[\w.]+)\s*[({]")]
    private static partial Regex NewInstanceDeclarationPattern();

    [GeneratedRegex(@"\.\s*Bind\s*\(\s*(?<argument>\w+)\s*\)")]
    private static partial Regex BindArgumentPattern();

    [GeneratedRegex(
        @"Configure\s*<\s*(?<type>[\w.]+)\s*>\s*\((?<tail>.{0,1600}?)\)\s*;",
        RegexOptions.Singleline)]
    private static partial Regex ConfigurePattern();

    [GeneratedRegex(@"\.\s*Get\s*<\s*(?<type>[\w.]+)\s*>\s*\(\s*\)")]
    private static partial Regex GetPattern();
}
