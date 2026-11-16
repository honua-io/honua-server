// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// Ratchet for interfaces under src/ that have one implementation, in the same project, and that no
// test names. Such an interface is an extra name for one class: use the class. Each baseline line is
// "declaring file|interface". The baseline may only shrink once it is on trunk. The scan is textual:
// comments and string literals are ignored, and a type counts as an implementation when the interface
// appears in its base list.
[Trait("Category", "Architecture")]
public sealed partial class SingleImplementationInterfaceRatchetTests
{
    private const string RelativeBaseline = "tests/dotnet/Honua.Architecture.Tests/single-implementation-interface-baseline.txt";

    [ArchitectureTest]
    public void SingleImplementationInterfaces_MustMatchTheBaseline()
    {
        var actual = Scan(ArchitectureTestHelpers.ResolveRepositoryRoot());
        var baseline = SourceTextRatchet.ReadEntries(BaselinePath());

        string.Join('\n', actual.Except(baseline, StringComparer.Ordinal).Order(StringComparer.Ordinal)).Should().BeEmpty(
            "an interface with one same-project implementation that no test names should be the class itself");
        string.Join('\n', baseline.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)).Should().BeEmpty(
            "delete baseline lines the scan no longer reports");
    }

    [ArchitectureTest]
    public void SingleImplementationInterfaceBaseline_MustNotGrowFromTheCheckedOutBase()
    {
        var prior = SourceTextRatchet.ReadParentEntries(ArchitectureTestHelpers.ResolveRepositoryRoot(), RelativeBaseline);
        if (prior is null)
        {
            return;
        }

        var current = SourceTextRatchet.ReadEntries(BaselinePath());
        string.Join('\n', current.Except(prior, StringComparer.Ordinal).Order(StringComparer.Ordinal)).Should().BeEmpty();
    }

    [ArchitectureTest]
    public void Scan_FindsOnlyTheUnnamedSameProjectSingleImplementation()
    {
        var root = Directory.CreateTempSubdirectory("single-impl-ratchet-").FullName;
        try
        {
            WriteSample(root);
            Scan(root).Should().Equal("src/A/IOnly.cs|IOnly");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static string[] Scan(string root)
    {
        var interfaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var implementations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var implementationFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in CSharpFiles(root, "src"))
        {
            var code = StripCommentsAndStrings(File.ReadAllText(file));
            foreach (Match match in InterfaceDeclaration().Matches(code))
            {
                Add(interfaces, match.Groups[1].Value, file);
            }

            foreach (var (type, baseName) in BaseListEntries(code))
            {
                if (type != baseName)
                {
                    Add(implementations, baseName, type + "|" + file);
                    implementationFiles[type + "|" + file] = file;
                }
            }
        }

        var namedInTests = TestIdentifiers(root);
        var projects = new Dictionary<string, string?>(StringComparer.Ordinal);
        return interfaces
            .Where(pair => pair.Value.Count == 1 && !namedInTests.Contains(pair.Key))
            .Where(pair => implementations.TryGetValue(pair.Key, out var files) && files.Count == 1
                && ProjectOf(pair.Value[0], projects) is { } project && project == ProjectOf(implementationFiles[files[0]], projects))
            .Select(pair => Path.GetRelativePath(root, pair.Value[0]).Replace('\\', '/') + "|" + pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> TestIdentifiers(string root)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in CSharpFiles(root, "tests"))
        {
            var code = Comment().Replace(File.ReadAllText(file), " ");
            words.UnionWith(Identifier().Matches(code).Select(match => match.Value));
        }

        return words;
    }

    // Yields (declared type, base type name) for every entry in every base list.
    private static IEnumerable<(string Type, string Base)> BaseListEntries(string code)
    {
        foreach (Match match in TypeDeclaration().Matches(code))
        {
            var index = SkipBalanced(code, SkipSpace(code, match.Index + match.Length), "<>");
            index = SkipBalanced(code, SkipSpace(code, index), "()");
            index = SkipSpace(code, index);
            if (index >= code.Length || code[index] != ':')
            {
                continue;
            }

            foreach (var entry in SplitTopLevel(BaseListText(code, index + 1)))
            {
                var name = GenericArguments().Replace(entry, string.Empty).Trim();
                yield return (match.Groups[1].Value, name[(name.LastIndexOf('.') + 1)..]);
            }
        }
    }

    private static string BaseListText(string code, int start)
    {
        var depth = 0;
        for (var index = start; index < code.Length; index++)
        {
            var character = code[index];
            depth += character is '<' or '(' ? 1 : character is '>' or ')' ? -1 : 0;
            if (depth == 0 && (character is '{' or ';' || WhereClause().IsMatch(code.AsSpan(index, Math.Min(7, code.Length - index)))))
            {
                return code[start..index];
            }
        }

        return code[start..];
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            depth += text[index] is '<' or '(' ? 1 : text[index] is '>' or ')' ? -1 : 0;
            if (depth == 0 && text[index] == ',')
            {
                yield return text[start..index];
                start = index + 1;
            }
        }

        yield return text[start..];
    }

    private static int SkipSpace(string code, int index)
    {
        while (index < code.Length && char.IsWhiteSpace(code[index]))
        {
            index++;
        }

        return index;
    }

    // Skips one bracketed group that starts at index; pair is the opening and closing character.
    private static int SkipBalanced(string code, int index, string pair)
    {
        if (index >= code.Length || code[index] != pair[0])
        {
            return index;
        }

        var depth = 0;
        for (; index < code.Length; index++)
        {
            depth += code[index] == pair[0] ? 1 : code[index] == pair[1] ? -1 : 0;
            if (depth == 0)
            {
                return index + 1;
            }
        }

        return index;
    }

    private static string? ProjectOf(string file, Dictionary<string, string?> cache)
    {
        var directory = Path.GetDirectoryName(file);
        if (directory is null)
        {
            return null;
        }

        if (!cache.TryGetValue(directory, out var project))
        {
            project = Directory.EnumerateFiles(directory, "*.csproj").Any() ? directory : ProjectOf(directory, cache);
            cache[directory] = project;
        }

        return project;
    }

    private static IEnumerable<string> CSharpFiles(string root, string folder)
    {
        var directory = Path.Join(root, folder);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains("/obj/", StringComparison.Ordinal) && !file.Contains("/bin/", StringComparison.Ordinal))
            : [];
    }

    private static string StripCommentsAndStrings(string text)
        => CommentOrString().Replace(text, " ");

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var values))
        {
            map[key] = values = [];
        }

        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    private static void WriteSample(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/A/A.csproj"] = "<Project />",
            ["src/A/IOnly.cs"] = "public interface IOnly { }\ninternal sealed class Only(int value) : IOnly { }\n",
            ["src/A/INamed.cs"] = "public interface INamed { }\npublic sealed class Named : INamed { }\n",
            ["src/A/ITwo.cs"] = "public interface ITwo<T> { }\nclass One : System.IDisposable, ITwo<int> { }\nclass Two : ITwo<string> { }\n",
            ["src/A/IRecord.cs"] = "public interface IRecord { }\nrecord struct First(int A) : IRecord;\nclass Second : IRecord { }\n",
            ["src/A/IPair.cs"] = "public interface IPair { }\nclass Real : IPair, System.IDisposable { }\nclass Null<T> : IPair where T : class { }\n",
            ["src/A/IComment.cs"] = "// interface INotDeclared\npublic interface IAcross { }\n",
            ["src/B/B.csproj"] = "<Project />",
            ["src/B/Across.cs"] = "public sealed class Across : IAcross { }\n",
            ["tests/A.Tests/T.cs"] = "class T { INamed named; }\n",
        };
        foreach (var (path, text) in files)
        {
            var full = Path.Join(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    private static string BaselinePath()
        => SourceTextRatchet.AbsolutePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), RelativeBaseline);

    [GeneratedRegex("""(?<![\w.])interface\s+(I[A-Z]\w*)""", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceDeclaration();

    [GeneratedRegex("""(?<![\w.])(?:record\s+(?:class|struct)|class|struct|interface|record)\s+([A-Za-z_]\w*)""", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex("""<[^<>]*(?:<[^<>]*>[^<>]*)*>""", RegexOptions.CultureInvariant)]
    private static partial Regex GenericArguments();

    [GeneratedRegex("""^\swhere\s""", RegexOptions.CultureInvariant)]
    private static partial Regex WhereClause();

    [GeneratedRegex("""@?"(?:\\.|[^"\\\n])*"|//[^\n]*|/\*.*?\*/""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CommentOrString();

    [GeneratedRegex("""//[^\n]*|/\*.*?\*/""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("""[A-Za-z_]\w*""", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
