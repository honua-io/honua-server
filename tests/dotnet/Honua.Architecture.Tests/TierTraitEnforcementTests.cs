// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Gate-reachability guard (honua-server#4410).
/// </summary>
/// <remarks>
/// <para>
/// The required per-PR gate selects server tests by trait —
/// <c>dotnet test tests/dotnet/Honua.Server.Tests --filter "Tier=Fast"</c>
/// (<c>.github/actions/lean-gate/action.yml</c>). <c>Tier</c> is emitted only by a TestKit
/// tier attribute (<c>[UnitTest]</c> → <c>Tier=Fast</c>, <c>[IntegrationTest]</c> →
/// <c>Tier=Integration</c>, <c>[ScaleTest]</c>/<c>[CloudTest]</c>/… → <c>Tier=Slow</c>).
/// A method written with a plain xUnit <c>[Fact]</c> carries no <c>Tier</c> trait at all and
/// is therefore invisible to every tier filter — it lands off the required check and nothing
/// tells the author.
/// </para>
/// <para>
/// This guard makes that a build failure for anything NEW. The methods that were already
/// untiered when the guard landed are listed, by fully-qualified name, in the committed
/// baseline <c>tests/dotnet/Honua.Architecture.Tests/tier-trait-baseline.txt</c>. The list is
/// a ratchet: adding a tier attribute to a baselined method must also remove its line, and a
/// stale line fails the guard, so the baseline can only shrink.
/// </para>
/// <para>
/// Regenerate the baseline (only when deliberately accepting new untiered methods — normally
/// you add the tier attribute instead):
/// <code>
/// HONUA_EMIT_TIER_TRAIT_BASELINE=1 dotnet test \
///   tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
///   --filter "FullyQualifiedName~TierTraitBaselineEmitter"
/// </code>
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class TierTraitEnforcementTests
{
    [ArchitectureTest]
    public void EveryTestMethod_MustCarryATierBearingAttribute_OrBeBaselined()
    {
        var untiered = TierTraitScanner.UntieredTestMethodNames();
        var baseline = TierTraitBaseline.Read();

        var newlyUntiered = untiered.Except(baseline, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        newlyUntiered.Should().BeEmpty(
            "every test method must carry a TestKit tier attribute so the required gate filter " +
            "(--filter \"Tier=<tier>\") can see it. A plain [Fact]/[Theory] emits no Tier trait and " +
            "lands off the required check. Replace [Fact] with [UnitTest] (Tier=Fast) for an " +
            "isolated test, [IntegrationTest] (Tier=Integration) for one that needs a host or a " +
            "database, or the matching [ScaleTest]/[CloudTest]/[EmulatorTest]/[ExternalServiceTest]/" +
            "[RoutingTest] (Tier=Slow). Newly untiered methods:\n" +
            string.Join("\n", newlyUntiered));
    }

    [ArchitectureTest]
    public void TierTraitBaseline_MustNotContainStaleEntries()
    {
        var untiered = TierTraitScanner.UntieredTestMethodNames();
        var baseline = TierTraitBaseline.Read();

        var stale = baseline.Except(untiered, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        stale.Should().BeEmpty(
            $"the tier-trait baseline ({TierTraitBaseline.RelativePath}) is a ratchet that may only " +
            "shrink. These entries name methods that are now tiered (or no longer exist), so their " +
            "lines must be deleted from the baseline in the same change:\n" +
            string.Join("\n", stale));
    }

    [ArchitectureTest]
    public void TierTraitBaseline_MustBeSortedAndUnique()
    {
        var lines = TierTraitBaseline.ReadLines();

        lines.Should().BeInAscendingOrder(
            StringComparer.Ordinal,
            $"{TierTraitBaseline.RelativePath} is machine-generated and must stay sorted so diffs are reviewable");
        lines.Should().OnlyHaveUniqueItems(
            $"{TierTraitBaseline.RelativePath} must not list the same method twice");
    }
}

/// <summary>
/// Regeneration entry point for <c>tier-trait-baseline.txt</c>, gated behind an environment
/// variable so ordinary <c>dotnet test</c> runs stay read-only (the same idiom as
/// <c>FeatureCatalogEmitter</c>).
/// </summary>
[Trait("Category", "Architecture")]
public sealed class TierTraitBaselineEmitter
{
    /// <summary>Environment variable that opts a run in to rewriting the baseline.</summary>
    public const string EmitEnvironmentVariable = "HONUA_EMIT_TIER_TRAIT_BASELINE";

    [Fact]
    public void Emit_TierTraitBaseline_FromLiveTestAssemblies()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EmitEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        TierTraitBaseline.Write(TierTraitScanner.UntieredTestMethodNames());
    }
}

/// <summary>
/// Reflection scan for xUnit test methods that emit no <c>Tier</c> trait.
/// </summary>
internal static class TierTraitScanner
{
    private const BindingFlags TestMemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// xUnit's own <c>[Fact]</c>/<c>[Theory]</c>. Everything that carries a tier derives from
    /// one of these AND from <c>ITraitAttribute</c> with a discoverer that emits <c>Tier</c>.
    /// </summary>
    private static readonly string[] XunitFactAttributeNames =
    [
        "Xunit.FactAttribute",
        "Xunit.TheoryAttribute"
    ];

    /// <summary>
    /// The assemblies whose gate reachability this guard owns. <c>Honua.Server.Tests</c> is the
    /// assembly the required lean gate filters (#4410); the extracted per-protocol assemblies
    /// are scanned too so a test moved out of Server.Tests cannot lose its tier on the way.
    /// </summary>
    internal static IReadOnlyList<Assembly> ScannedAssemblies()
        => ArchitectureTestHelpers.IntegrationTestAssemblies();

    /// <summary>
    /// Fully-qualified <c>Namespace.Type.Method</c> names of every discovered test method that
    /// carries no tier-bearing attribute, sorted ordinally.
    /// </summary>
    internal static IReadOnlyCollection<string> UntieredTestMethodNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var assembly in ScannedAssemblies())
        {
            foreach (var type in ArchitectureTestHelpers.GetTypesSafely(assembly))
            {
                if (type is null || type.IsAbstract && type.IsSealed)
                {
                    continue;
                }

                // A class-level [Trait("Category","Architecture")] opts the class out: the
                // architecture smoke job selects it by Category, not by Tier.
                if (HasArchitectureCategoryTrait(type))
                {
                    continue;
                }

                foreach (var method in type.GetMethods(TestMemberFlags))
                {
                    if (!IsXunitTestMethod(method) || HasTierBearingAttribute(method) || HasArchitectureCategoryTrait(method))
                    {
                        continue;
                    }

                    names.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        return names;
    }

    private static bool IsXunitTestMethod(MethodInfo method)
        => method.GetCustomAttributes(inherit: true)
            .Any(attribute => IsOrDerivesFromXunitFact(attribute.GetType()));

    private static bool IsOrDerivesFromXunitFact(Type attributeType)
    {
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            if (XunitFactAttributeNames.Contains(current.FullName, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the member carries an attribute whose trait discoverer emits a <c>Tier</c>
    /// trait. Resolved from the attribute's own <c>[TraitDiscoverer]</c> metadata rather than
    /// a hard-coded attribute list, so a new TestKit tier attribute is honoured the day it is
    /// added and one that stops emitting <c>Tier</c> stops satisfying the guard.
    /// </summary>
    private static bool HasTierBearingAttribute(MemberInfo member)
        => member.GetCustomAttributes(inherit: true)
            .Any(attribute => EmitsTierTrait(attribute.GetType()));

    private static readonly Dictionary<Type, bool> _tierEmittingCache = [];

    private static bool EmitsTierTrait(Type attributeType)
    {
        lock (_tierEmittingCache)
        {
            if (_tierEmittingCache.TryGetValue(attributeType, out var cached))
            {
                return cached;
            }

            var emits = ResolveEmitsTierTrait(attributeType);
            _tierEmittingCache[attributeType] = emits;
            return emits;
        }
    }

    private static bool ResolveEmitsTierTrait(Type attributeType)
    {
        var discovererAttribute = attributeType.GetCustomAttributes(inherit: true)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.GetType().FullName, "Xunit.Sdk.TraitDiscovererAttribute", StringComparison.Ordinal));

        if (discovererAttribute is null)
        {
            return false;
        }

        var typeName = (string?)discovererAttribute.GetType()
            .GetProperty("TypeName")?.GetValue(discovererAttribute);
        var assemblyName = (string?)discovererAttribute.GetType()
            .GetProperty("AssemblyName")?.GetValue(discovererAttribute);

        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        Type? discovererType;
        try
        {
            discovererType = Assembly.Load(assemblyName).GetType(typeName);
        }
        catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException or TypeLoadException)
        {
            return false;
        }

        if (discovererType is null)
        {
            return false;
        }

        var getTraits = discovererType.GetMethod("GetTraits");
        if (getTraits is null)
        {
            return false;
        }

        object? discoverer;
        try
        {
            discoverer = Activator.CreateInstance(discovererType);
        }
        catch (MissingMethodException)
        {
            return false;
        }

        if (discoverer is null)
        {
            return false;
        }

        try
        {
            // The TestKit discoverers ignore their IAttributeInfo argument, so a null is safe
            // and avoids taking a dependency on xUnit's reflection wrappers here.
            if (getTraits.Invoke(discoverer, [null]) is not System.Collections.IEnumerable traits)
            {
                return false;
            }

            foreach (var trait in traits)
            {
                var key = trait.GetType().GetProperty("Key")?.GetValue(trait) as string;
                if (string.Equals(key, "Tier", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (TargetInvocationException)
        {
            // A discoverer that genuinely needs its argument cannot be probed this way; treat
            // it as non-tier-bearing rather than crashing the guard.
            return false;
        }

        return false;
    }

    private static bool HasArchitectureCategoryTrait(MemberInfo member)
        => member.GetCustomAttributes(inherit: true).Any(attribute =>
        {
            if (!string.Equals(attribute.GetType().FullName, "Xunit.TraitAttribute", StringComparison.Ordinal))
            {
                return false;
            }

            var type = attribute.GetType();
            var name = type.GetProperty("Name")?.GetValue(attribute) as string;
            var value = type.GetProperty("Value")?.GetValue(attribute) as string;
            return string.Equals(name, "Category", StringComparison.Ordinal) &&
                   string.Equals(value, "Architecture", StringComparison.Ordinal);
        });
}

/// <summary>
/// Reads and writes the committed tier-trait baseline.
/// </summary>
internal static class TierTraitBaseline
{
    /// <summary>Repo-relative location of the committed baseline.</summary>
    public const string RelativePath = "tests/dotnet/Honua.Architecture.Tests/tier-trait-baseline.txt";

    private const string Header =
        "# Generated by TierTraitBaselineEmitter (honua-server#4410). Do not hand-edit except to DELETE lines.\n" +
        "# Every line is a test method that emits no Tier trait and is therefore invisible to the\n" +
        "# required gate filter. Give the method a TestKit tier attribute and delete its line here.\n" +
        "# The guard fails on any entry that is no longer untiered, so this file can only shrink.\n";

    public static string AbsolutePath()
        => ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            "tests",
            "dotnet",
            "Honua.Architecture.Tests",
            "tier-trait-baseline.txt");

    public static IReadOnlyList<string> ReadLines()
    {
        var path = AbsolutePath();
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    public static HashSet<string> Read()
        => new(ReadLines(), StringComparer.Ordinal);

    public static void Write(IReadOnlyCollection<string> names)
    {
        var ordered = names.OrderBy(name => name, StringComparer.Ordinal);
        File.WriteAllText(
            AbsolutePath(),
            Header + string.Join('\n', ordered) + (names.Count > 0 ? "\n" : string.Empty),
            System.Text.Encoding.UTF8);
    }
}
