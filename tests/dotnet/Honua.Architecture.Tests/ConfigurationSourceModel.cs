// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Shared, purely syntactic model of the <c>src/</c> tree used by the configuration guards:
/// <see cref="ConfigurationBindingShapeTests"/> (source-generated binding, #3055) and
/// <see cref="HandParsedConfigurationSectionTests"/> (hand-rolled section parsers, #3315).
/// </summary>
/// <remarks>
/// Both guards need the same primitives — a comment/literal stripper, brace matching, an
/// auto-property scan, and simple-name-to-declaration resolution — so they live here rather than
/// being duplicated (or re-derived slightly differently) per guard.
/// </remarks>
internal static partial class ConfigurationSourceModel
{
    /// <summary>A public auto-property declared on a type in <c>src/</c>.</summary>
    internal sealed record PropertyShape(string Name, string TypeName, bool IsInitOnly);

    /// <summary>A class or record declared in <c>src/</c>, with its public auto-properties.</summary>
    internal sealed record TypeShape(
        string Name,
        string Namespace,
        string Path,
        IReadOnlySet<string> UsingNamespaces,
        IReadOnlyList<string> BaseTypeNames,
        IReadOnlyList<PropertyShape> Properties)
    {
        public string QualifiedName => string.IsNullOrEmpty(Namespace)
            ? Name
            : $"{Namespace}.{Name}";
    }

    /// <summary>
    /// Reads every non-artifact <c>*.cs</c> file under <c>src/</c>, keyed by repository-relative path.
    /// </summary>
    internal static Dictionary<string, string> LoadSourceFiles()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repositoryRoot, "src");
        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .ToDictionary(
                path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);
    }

    internal static Dictionary<string, List<TypeShape>> ParseTypes(
        IReadOnlyDictionary<string, string> sources)
    {
        var typesByName = new Dictionary<string, List<TypeShape>>(StringComparer.Ordinal);
        foreach (var (path, source) in sources.Select(pair =>
                     (pair.Key, Source: StripCommentsAndLiterals(pair.Value))))
        {
            var namespaceName = MatchNamespace(source);
            var usingNamespaces = MatchUsingNamespaces(source);

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

                var declarationTail = typeMatch.Groups["tail"].Value;
                var baseClauseSeparator = declarationTail.IndexOf(':', StringComparison.Ordinal);
                var baseTypeNames = baseClauseSeparator < 0
                    ? []
                    : QualifiedTypeIdentifierPattern()
                        .Matches(declarationTail[(baseClauseSeparator + 1)..])
                        .Select(match => match.Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

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
                    baseTypeNames,
                    properties));
            }
        }

        return typesByName;
    }

    /// <summary>
    /// Resolves a possibly-unqualified type name to the declarations it can refer to, preferring the
    /// referring file's own namespace and then its <c>using</c> imports.
    /// </summary>
    /// <param name="candidateName">The (possibly unqualified) type name to resolve.</param>
    /// <param name="preferredNamespace">Namespace of the file making the reference.</param>
    /// <param name="usingNamespaces">Namespaces imported by that file.</param>
    /// <param name="typesByName">All declarations parsed from <c>src/</c>, keyed by simple name.</param>
    /// <param name="ambiguous">
    /// Set when the simple name matches several declarations that neither the preferred namespace nor
    /// the imports disambiguate. The walk cannot follow such a reference, so callers report it rather
    /// than dropping it silently — that silent drop is the #3306 reachability blind spot.
    /// </param>
    internal static IEnumerable<TypeShape> ResolveDeclaredTypes(
        string candidateName,
        string preferredNamespace,
        IReadOnlySet<string> usingNamespaces,
        IReadOnlyDictionary<string, List<TypeShape>> typesByName,
        out bool ambiguous)
    {
        ambiguous = false;
        var simpleName = SimpleTypeName(candidateName);
        if (!typesByName.TryGetValue(simpleName, out var shapes))
        {
            return [];
        }

        if (candidateName.Contains('.', StringComparison.Ordinal))
        {
            var exact = shapes
                .Where(shape =>
                    string.Equals(shape.QualifiedName, candidateName, StringComparison.Ordinal))
                .ToArray();
            if (exact.Length > 0)
            {
                return exact;
            }

            // Partially-qualified reference, e.g. `Core.Features.Import.Domain.ImportLimits` written
            // from a file in namespace `Honua.Db.Postgres`. C# completes such a name from an
            // enclosing namespace or a `using`, and so must the walk — treating it as unresolvable
            // is how the Import:Limits parser stayed invisible to the guards (#3315).
            var suffix = "." + candidateName;
            var qualifyingPrefixes = EnclosingNamespaces(preferredNamespace)
                .Concat(usingNamespaces)
                .ToHashSet(StringComparer.Ordinal);
            var partiallyQualified = shapes
                .Where(shape =>
                    shape.QualifiedName.EndsWith(suffix, StringComparison.Ordinal) &&
                    qualifyingPrefixes.Contains(shape.QualifiedName[..^suffix.Length]))
                .ToArray();
            var distinctPartialCount = partiallyQualified
                .Select(shape => shape.QualifiedName)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (distinctPartialCount == 1)
            {
                return partiallyQualified;
            }

            ambiguous = distinctPartialCount > 1;
            return [];
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

        if (shapes.Select(shape => shape.QualifiedName).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            return shapes;
        }

        ambiguous = true;
        return [];
    }

    internal static IEnumerable<TypeShape> ResolveDeclaredTypes(
        string candidateName,
        string preferredNamespace,
        IReadOnlySet<string> usingNamespaces,
        IReadOnlyDictionary<string, List<TypeShape>> typesByName)
        => ResolveDeclaredTypes(candidateName, preferredNamespace, usingNamespaces, typesByName, out _);

    /// <summary>
    /// Yields <paramref name="namespaceName"/> and each of its ancestor namespaces, which are the
    /// prefixes C# will try when completing a partially-qualified type name.
    /// </summary>
    private static IEnumerable<string> EnclosingNamespaces(string namespaceName)
    {
        var current = namespaceName;
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            var separator = current.LastIndexOf('.');
            current = separator < 0 ? string.Empty : current[..separator];
        }
    }

    internal static string MatchNamespace(string strippedSource)
        => NamespacePattern().Match(strippedSource).Groups["namespace"].Value;

    internal static HashSet<string> MatchUsingNamespaces(string strippedSource)
        => UsingNamespacePattern()
            .Matches(strippedSource)
            .Select(match => match.Groups["namespace"].Value)
            .ToHashSet(StringComparer.Ordinal);

    internal static int FindMatchingBrace(string source, int openBrace)
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

    internal static int BraceDepthAt(string body, int targetIndex)
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

    internal static bool IsBuildArtifact(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SimpleTypeName(string typeName)
    {
        var separator = typeName.LastIndexOf('.');
        return separator < 0 ? typeName : typeName[(separator + 1)..];
    }

    /// <summary>
    /// Blanks comments while preserving string literals and total length, so an offset found in the
    /// <see cref="StripCommentsAndLiterals"/> view of the same file indexes correctly into this one.
    /// Used where the guard needs the literal text (configuration keys) but must do its structural
    /// scanning on the literal-free view.
    /// </summary>
    internal static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var current = source[index];
            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    result.Append(' ');
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                result.Append("  ");
                index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
                    {
                        result.Append("  ");
                        index += 2;
                        break;
                    }

                    result.Append(source[index] == '\n' ? '\n' : ' ');
                    index++;
                }

                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Blanks comments, string literals, and character literals, preserving total length and line
    /// structure so brace matching and declaration scanning cannot be misled by their contents.
    /// </summary>
    internal static string StripCommentsAndLiterals(string source)
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

    [GeneratedRegex(
        @"\b(?:class|record(?:\s+class)?)\s+(?<name>[A-Za-z_]\w*)(?<tail>[^\{;]*)(?<brace>\{)",
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
    internal static partial Regex QualifiedTypeIdentifierPattern();
}
