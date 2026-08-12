// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Protects source-generated configuration binding from init-only properties (#3055).
/// </summary>
/// <remarks>
/// Covered registration shapes: <c>Configure&lt;T&gt;(IConfiguration)</c>,
/// <c>AddOptions&lt;T&gt;().Bind(...)</c> / <c>.BindConfiguration(...)</c> (chained or through a
/// deferred builder local), <c>GetSection(T.SectionName).Bind(instance)</c>, and
/// <c>section.Bind(local)</c> where the local is created with <c>new T()</c>. All of these bind
/// onto an existing instance, where the source-generated binder silently skips init-only
/// properties.
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

    [ArchitectureTest]
    public void ConfigureBoundOptionGraphs_MustNotDeclareInitOnlyProperties()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repositoryRoot, "src");
        var sources = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .ToDictionary(
                path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);

        var typesByName = ParseTypes(sources);
        var boundRootNames = DiscoverConfigurationBoundRoots(sources.Values, typesByName);
        var boundRootSimpleNames = boundRootNames
            .Select(SimpleTypeName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var auditedRootName in AuditedRootNames)
        {
            Assert.Contains(auditedRootName, boundRootSimpleNames);
        }

        // Collision anchor: the Postgres registration imports the configuration type;
        // the unrelated per-request cache model must not enter the mutable binding graph.
        Assert.Contains("Honua.Core.Features.Infrastructure.Domain.QueryCacheOptions", boundRootNames);
        Assert.DoesNotContain("Honua.Infrastructure.Caching.QueryCacheOptions", boundRootNames);

        var pending = new Stack<TypeShape>(
            typesByName.Values
                .SelectMany(static shapes => shapes)
                .Where(shape => boundRootNames.Contains(shape.QualifiedName)));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var violations = new SortedSet<string>(StringComparer.Ordinal);

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

                foreach (var referencedType in ResolveReferencedTypes(property.TypeName, typeShape, typesByName))
                {
                    pending.Push(referencedType);
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Types bound through Configure<T>(IConfiguration) or AddOptions<T>().Bind must use " +
            "ordinary setters throughout their reachable object graph. Init-only properties: " +
            string.Join(", ", violations));
    }

    private static HashSet<string> DiscoverConfigurationBoundRoots(
        IEnumerable<string> sources,
        IReadOnlyDictionary<string, List<TypeShape>> typesByName)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.Select(StripCommentsAndLiterals))
        {
            var namespaceName = NamespacePattern().Match(source).Groups["namespace"].Value;
            var usingNamespaces = UsingNamespacePattern()
                .Matches(source)
                .Select(match => match.Groups["namespace"].Value)
                .ToHashSet(StringComparer.Ordinal);
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
                             !string.Equals(SimpleTypeName(candidate.Name), "TOptions", StringComparison.Ordinal) &&
                             ContainsConfigurationBind(candidate.Tail))
                         .Select(candidate => candidate.Name))
            {
                candidateTypeNames.Add(typeName);
            }

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
                ResolveDeclaredTypes(typeName, namespaceName, usingNamespaces, typesByName))
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

    private static Dictionary<string, List<TypeShape>> ParseTypes(
        IReadOnlyDictionary<string, string> sources)
    {
        var typesByName = new Dictionary<string, List<TypeShape>>(StringComparer.Ordinal);
        foreach (var (path, source) in sources.Select(pair =>
                     (pair.Key, Source: StripCommentsAndLiterals(pair.Value))))
        {
            var namespaceName = NamespacePattern().Match(source).Groups["namespace"].Value;
            var usingNamespaces = UsingNamespacePattern()
                .Matches(source)
                .Select(match => match.Groups["namespace"].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match typeMatch in TypeDeclarationPattern().Matches(source))
            {
                var openBrace = typeMatch.Groups["brace"].Index;
                var closeBrace = FindMatchingBrace(source, openBrace);
                if (closeBrace < 0)
                {
                    continue;
                }

                var body = source[(openBrace + 1)..closeBrace];
                var properties = new List<PropertyShape>();
                foreach (Match propertyMatch in AutoPropertyPattern()
                             .Matches(body)
                             .Where(propertyMatch => BraceDepthAt(body, propertyMatch.Index) == 0))
                {
                    properties.Add(new PropertyShape(
                        propertyMatch.Groups["name"].Value,
                        propertyMatch.Groups["type"].Value,
                        string.Equals(
                            propertyMatch.Groups["setter"].Value,
                            "init",
                            StringComparison.Ordinal)));
                }

                var typeName = typeMatch.Groups["name"].Value;
                if (!typesByName.TryGetValue(typeName, out var shapes))
                {
                    shapes = [];
                    typesByName.Add(typeName, shapes);
                }

                shapes.Add(new TypeShape(
                    typeName,
                    namespaceName,
                    path,
                    usingNamespaces,
                    properties));
            }
        }

        return typesByName;
    }

    private static IEnumerable<TypeShape> ResolveReferencedTypes(
        string propertyTypeName,
        TypeShape declaringType,
        IReadOnlyDictionary<string, List<TypeShape>> typesByName)
    {
        foreach (var candidateName in QualifiedTypeIdentifierPattern()
                     .Matches(propertyTypeName)
                     .Select(match => match.Value)
                     .Distinct(StringComparer.Ordinal))
        {
            var simpleName = SimpleTypeName(candidateName);
            if (!typesByName.TryGetValue(simpleName, out var shapes))
            {
                continue;
            }

            foreach (var shape in ResolveDeclaredTypes(
                         candidateName,
                         declaringType.Namespace,
                         declaringType.UsingNamespaces,
                         typesByName))
            {
                yield return shape;
            }
        }
    }

    private static IEnumerable<TypeShape> ResolveDeclaredTypes(
        string candidateName,
        string preferredNamespace,
        IReadOnlySet<string> usingNamespaces,
        IReadOnlyDictionary<string, List<TypeShape>> typesByName)
    {
        var simpleName = SimpleTypeName(candidateName);
        if (!typesByName.TryGetValue(simpleName, out var shapes))
        {
            return [];
        }

        if (candidateName.Contains('.', StringComparison.Ordinal))
        {
            return shapes.Where(shape =>
                string.Equals(shape.QualifiedName, candidateName, StringComparison.Ordinal));
        }

        var sameNamespace = shapes
            .Where(shape => string.Equals(shape.Namespace, preferredNamespace, StringComparison.Ordinal))
            .ToArray();
        if (sameNamespace.Length > 0)
        {
            return sameNamespace;
        }

        var imported = shapes
            .Where(shape => usingNamespaces.Contains(shape.Namespace))
            .ToArray();
        if (imported.Select(shape => shape.QualifiedName).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            return imported;
        }

        return shapes.Select(shape => shape.QualifiedName).Distinct(StringComparer.Ordinal).Count() == 1
            ? shapes
            : [];
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        var depth = 0;
        for (var index = openBrace; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return -1;
    }

    private static int BraceDepthAt(string body, int targetIndex)
    {
        var depth = 0;
        for (var index = 0; index < targetIndex; index++)
        {
            if (body[index] == '{')
            {
                depth++;
            }
            else if (body[index] == '}')
            {
                depth--;
            }
        }

        return depth;
    }

    private static bool IsBuildArtifact(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string SimpleTypeName(string typeName)
    {
        var separator = typeName.LastIndexOf('.');
        return separator < 0 ? typeName : typeName[(separator + 1)..];
    }

    private static string StripCommentsAndLiterals(string source)
    {
        var result = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                AppendSpaces(result, 2);
                index += 2;
                while (index < source.Length && source[index] != '\n')
                {
                    result.Append(' ');
                    index++;
                }

                if (index < source.Length)
                {
                    result.Append('\n');
                }

                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                AppendSpaces(result, 2);
                index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
                    {
                        AppendSpaces(result, 2);
                        index++;
                        break;
                    }

                    result.Append(source[index] == '\n' ? '\n' : ' ');
                    index++;
                }

                continue;
            }

            if (current == '@' && index + 1 < source.Length && source[index + 1] == '"')
            {
                AppendSpaces(result, 2);
                index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '"')
                    {
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            AppendSpaces(result, 2);
                            index += 2;
                            continue;
                        }

                        result.Append(' ');
                        break;
                    }

                    result.Append(source[index] == '\n' ? '\n' : ' ');
                    index++;
                }

                continue;
            }

            if (current == '"')
            {
                var quoteCount = CountRun(source, index, '"');
                if (quoteCount >= 3)
                {
                    AppendSpaces(result, quoteCount);
                    index += quoteCount;
                    while (index < source.Length)
                    {
                        var closingCount = source[index] == '"' ? CountRun(source, index, '"') : 0;
                        if (closingCount >= quoteCount)
                        {
                            AppendSpaces(result, quoteCount);
                            index += quoteCount - 1;
                            break;
                        }

                        result.Append(source[index] == '\n' ? '\n' : ' ');
                        index++;
                    }

                    continue;
                }

                result.Append(' ');
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        AppendSpaces(result, 2);
                        index += 2;
                        continue;
                    }

                    result.Append(source[index] == '\n' ? '\n' : ' ');
                    if (source[index] == '"')
                    {
                        break;
                    }

                    index++;
                }

                continue;
            }

            if (current == '\'')
            {
                result.Append(' ');
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        AppendSpaces(result, 2);
                        index += 2;
                        continue;
                    }

                    result.Append(source[index] == '\n' ? '\n' : ' ');
                    if (source[index] == '\'')
                    {
                        break;
                    }

                    index++;
                }

                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static int CountRun(string source, int start, char value)
    {
        var count = 0;
        while (start + count < source.Length && source[start + count] == value)
        {
            count++;
        }

        return count;
    }

    private static void AppendSpaces(StringBuilder builder, int count)
        => builder.Append(' ', count);

    private sealed record PropertyShape(string Name, string TypeName, bool IsInitOnly);

    private sealed record TypeShape(
        string Name,
        string Namespace,
        string Path,
        IReadOnlySet<string> UsingNamespaces,
        IReadOnlyList<PropertyShape> Properties)
    {
        public string QualifiedName => string.IsNullOrEmpty(Namespace)
            ? Name
            : $"{Namespace}.{Name}";
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

    [GeneratedRegex(
        @"\b(?:class|record(?:\s+class)?)\s+(?<name>[A-Za-z_]\w*)[^\{;]*(?<brace>\{)",
        RegexOptions.Singleline)]
    private static partial Regex TypeDeclarationPattern();

    [GeneratedRegex(
        @"\bpublic\s+(?:required\s+)?(?<type>[A-Za-z_][\w.\s<>,?\[\]]*?)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*get\s*;\s*(?<setter>init|set)\s*;\s*\}",
        RegexOptions.Singleline)]
    private static partial Regex AutoPropertyPattern();

    [GeneratedRegex(@"\bnamespace\s+(?<namespace>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*[;{]")]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(
        @"^\s*using\s+(?<namespace>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;",
        RegexOptions.Multiline)]
    private static partial Regex UsingNamespacePattern();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*\b")]
    private static partial Regex QualifiedTypeIdentifierPattern();
}
