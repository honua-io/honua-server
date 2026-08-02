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
[Trait("Category", "Architecture")]
public sealed partial class ConfigurationBindingShapeTests
{
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

        var boundRootNames = DiscoverConfigurationBoundRoots(sources.Values);
        foreach (var auditedRootName in AuditedRootNames)
        {
            Assert.Contains(auditedRootName, boundRootNames);
        }

        var typesByName = ParseTypes(sources);
        var pending = new Stack<string>(boundRootNames);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        while (pending.TryPop(out var typeName))
        {
            if (!visited.Add(typeName) || !typesByName.TryGetValue(typeName, out var typeShapes))
            {
                continue;
            }

            foreach (var typeShape in typeShapes)
            {
                foreach (var property in typeShape.Properties)
                {
                    if (property.IsInitOnly)
                    {
                        violations.Add($"{typeShape.Path}: {typeShape.Name}.{property.Name}");
                    }

                    foreach (Match match in TypeIdentifierPattern().Matches(property.TypeName))
                    {
                        var referencedTypeName = match.Value;
                        if (typesByName.ContainsKey(referencedTypeName))
                        {
                            pending.Push(referencedTypeName);
                        }
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Types bound through Configure<T>(IConfiguration) or AddOptions<T>().Bind must use " +
            "ordinary setters throughout their reachable object graph. Init-only properties: " +
            string.Join(", ", violations));
    }

    private static HashSet<string> DiscoverConfigurationBoundRoots(IEnumerable<string> sources)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawSource in sources)
        {
            var source = StripCommentsAndLiterals(rawSource);

            foreach (Match match in ChainedAddOptionsPattern().Matches(source))
            {
                if (ContainsConfigurationBind(match.Groups["tail"].Value))
                {
                    roots.Add(SimpleTypeName(match.Groups["type"].Value));
                }
            }

            var boundVariables = VariableBindPattern()
                .Matches(source)
                .Select(match => match.Groups["variable"].Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (Match match in AddOptionsVariablePattern().Matches(source))
            {
                if (boundVariables.Contains(match.Groups["variable"].Value))
                {
                    roots.Add(SimpleTypeName(match.Groups["type"].Value));
                }
            }

            foreach (Match match in ConfigurePattern().Matches(source))
            {
                var typeName = SimpleTypeName(match.Groups["type"].Value);
                if (!string.Equals(typeName, "TOptions", StringComparison.Ordinal) &&
                    ContainsConfigurationBind(match.Groups["tail"].Value))
                {
                    roots.Add(typeName);
                }
            }
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
        foreach (var (path, rawSource) in sources)
        {
            var source = StripCommentsAndLiterals(rawSource);
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
                foreach (Match propertyMatch in AutoPropertyPattern().Matches(body))
                {
                    if (BraceDepthAt(body, propertyMatch.Index) != 0)
                    {
                        continue;
                    }

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

                shapes.Add(new TypeShape(typeName, path, properties));
            }
        }

        return typesByName;
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
        string Path,
        IReadOnlyList<PropertyShape> Properties);

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

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]*\b")]
    private static partial Regex TypeIdentifierPattern();
}
