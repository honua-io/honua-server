// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Protects hand-rolled configuration parsers from silently drifting behind the type they
/// populate (#3315), the sibling of the source-generated-binder hole guarded by
/// <see cref="ConfigurationBindingShapeTests"/> (#3055).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConfigurationBindingShapeTests"/> can only see types reachable from a
/// <c>Configure&lt;T&gt;</c>/<c>Bind</c> root. A type that is instead built by hand — an object
/// initializer whose values come from <c>section["Key"]</c>, <c>section.GetValue&lt;T&gt;("Key")</c>,
/// or <c>section.GetSection("Key")</c> reads — is invisible to it by construction. The
/// <c>Import:Limits</c> parser drifted six properties behind
/// <c>Honua.Core.Features.Import.Domain.ImportLimits</c> exactly that way: operators set the keys,
/// nothing failed, and the defaults were used anyway.
/// </para>
/// <para>
/// This guard is deliberately shape-driven rather than type-driven: it discovers every hand parser
/// in <c>src/</c> and requires it to read a configuration key for every settable property of the
/// type it constructs, so the next options class built this way is covered without anyone
/// remembering to add it here.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed partial class HandParsedConfigurationSectionTests
{
    /// <summary>
    /// Drift anchor, not the discovery mechanism: parsers are discovered from source on every run,
    /// but these known hand parsers must keep being discovered or the extraction patterns have
    /// rotted and the guard has quietly stopped guarding anything.
    /// </summary>
    private static readonly string[] AuditedHandParsedTypeNames =
    [
        // The #3315 parser: src/Honua.Db/Postgres/ServiceCollectionExtensions.cs.
        "Honua.Core.Features.Import.Domain.ImportLimits",
        // A second, independently written parser the shape-driven scan finds on its own
        // (src/Honua.Server/Features/FileStorage/FileStorageServiceCollectionExtensions.cs) —
        // evidence that this guard is not ImportLimits-specific.
        "Honua.Core.Features.Infrastructure.Domain.LocalStorageOptions",
    ];

    /// <summary>
    /// Escape hatch for a parser that legitimately populates only part of its target type — for
    /// example when the remaining properties are computed elsewhere rather than configured. Keyed
    /// by the constructed type's qualified name; the value is the reason, which is the point of the
    /// entry. Empty today: every hand parser in <c>src/</c> reads every key it should.
    /// </summary>
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

    /// <summary>
    /// Collects the configuration keys an initializer body reads. A hierarchical key
    /// (<c>"LocalStorage:BasePath"</c>) also contributes its leaf segment, because that is the
    /// segment that corresponds to the property being assigned.
    /// </summary>
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
