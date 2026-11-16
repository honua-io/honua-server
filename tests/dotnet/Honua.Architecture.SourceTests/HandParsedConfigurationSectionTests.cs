// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed partial class HandParsedConfigurationSectionTests
{
    private static readonly string[] AuditedHandParsedTypeNames =
    [
        // The #3315 parser: src/Honua.Db/Postgres/ServiceCollectionExtensions.cs.
        "Honua.Core.Features.Import.Domain.ImportLimits",
        // A second, independently written parser the shape-driven scan finds on its own
        // (src/Honua.Server/Features/FileStorage/FileStorageServiceCollectionExtensions.cs) —
        // evidence that this guard is not ImportLimits-specific.
        "Honua.Core.Features.Infrastructure.Domain.LocalStorageOptions",
    ];

    private static readonly Dictionary<string, string> JustifiedPartialHandParsers =
        new(StringComparer.Ordinal);

    [ArchitectureTest]
    public void HandParsedConfigurationSections_MustReadEveryTargetProperty()
    {
        var sources = ConfigurationSourceModel.LoadSourceFiles();
        var typesByName = ConfigurationSourceModel.ParseTypes(sources);
        var discovered = new SortedSet<string>(StringComparer.Ordinal);
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (path, source) in sources)
        {
            // Two length-preserving views of the same file: `structural` has string literals
            // blanked so brace matching cannot be thrown off by a literal containing `{`, while
            // `readable` keeps literals so the configuration keys are still recoverable. Because
            // both strippers preserve length, offsets found in one index correctly into the other.
            var structural = ConfigurationSourceModel.StripCommentsAndLiterals(source);
            var readable = ConfigurationSourceModel.StripComments(source);
            var namespaceName = ConfigurationSourceModel.MatchNamespace(structural);
            var usingNamespaces = ConfigurationSourceModel.MatchUsingNamespaces(structural);

            foreach (Match match in ObjectInitializerPattern().Matches(structural))
            {
                var openBrace = match.Groups["brace"].Index;
                var closeBrace = ConfigurationSourceModel.FindMatchingBrace(structural, openBrace);
                if (closeBrace < 0)
                {
                    continue;
                }

                var body = readable[(openBrace + 1)..closeBrace];
                var keys = ReadConfigurationKeys(body);
                if (keys.Count == 0)
                {
                    continue;
                }

                foreach (var shape in ConfigurationSourceModel.ResolveDeclaredTypes(
                             match.Groups["type"].Value,
                             namespaceName,
                             usingNamespaces,
                             typesByName))
                {
                    discovered.Add(shape.QualifiedName);
                    if (JustifiedPartialHandParsers.ContainsKey(shape.QualifiedName))
                    {
                        continue;
                    }

                    var missing = shape.Properties
                        .Select(property => property.Name)
                        .Where(name => !keys.Contains(name))
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        violations.Add(
                            $"{path}: {shape.QualifiedName} never reads {string.Join(", ", missing)}");
                    }
                }
            }
        }

        foreach (var auditedTypeName in AuditedHandParsedTypeNames)
        {
            Assert.Contains(auditedTypeName, discovered);
        }

        Assert.True(
            violations.Count == 0,
            "A hand-rolled configuration parser must read a key for every settable property of the " +
            "type it builds, or the operator's setting is silently ignored (#3315). Either read the " +
            "key, drop the property, or add a justified entry to JustifiedPartialHandParsers. " +
            "Unread properties: " + string.Join("; ", violations));
    }

    private static HashSet<string> ReadConfigurationKeys(string initializerBody)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in SectionIndexerPattern().Matches(initializerBody))
        {
            AddKey(keys, match.Groups["key"].Value);
        }

        foreach (Match match in SectionAccessorPattern().Matches(initializerBody))
        {
            AddKey(keys, match.Groups["key"].Value);
        }

        return keys;

        static void AddKey(HashSet<string> keys, string key)
        {
            keys.Add(key);
            var separator = key.LastIndexOf(':');
            if (separator >= 0 && separator + 1 < key.Length)
            {
                keys.Add(key[(separator + 1)..]);
            }
        }
    }

    [GeneratedRegex(@"\bnew\s+(?<type>[\w.]+)\s*(?:\(\s*\))?\s*(?<brace>\{)")]
    private static partial Regex ObjectInitializerPattern();

    [GeneratedRegex(@"\b\w*(?:[Ss]ection|[Cc]onfiguration)\s*\[\s*""(?<key>[^""]+)""\s*\]")]
    private static partial Regex SectionIndexerPattern();

    [GeneratedRegex(
        @"\b\w*(?:[Ss]ection|[Cc]onfiguration)\s*\.\s*(?:GetValue\s*<[^>]*>|GetValue|GetSection)\s*\(\s*""(?<key>[^""]+)""")]
    private static partial Regex SectionAccessorPattern();
}
