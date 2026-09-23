// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Honua.TestKit.Attributes;
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
/// untiered when the guard landed are listed, by fully-qualified method signature, in the committed
/// baseline <c>tests/dotnet/Honua.Architecture.Tests/tier-trait-baseline.txt</c>. The list is
/// a ratchet: adding a tier attribute to a baselined method must also remove its line, and a
/// stale line fails the guard. The required gate compares the file to the first parent of
/// its checked-out merge commit, so adding a new bare test and its baseline line also fails.
/// </para>
/// <para>
/// Regenerate the baseline after tiering existing methods, then commit the resulting deletions:
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
    public void TierTraitBaseline_MustNotGrowFromTheCheckedOutBase()
    {
        // PR Gate checks out refs/pull/N/merge with fetch-depth 2. Its first parent is the
        // trusted trunk tip, even when the PR adds a bare [Fact] and its baseline line together.
        // The first PR introducing this file has no parent baseline and is allowed to seed it.
        var prior = TierTraitBaseline.ReadFromFirstParent(ArchitectureTestHelpers.ResolveRepositoryRoot());
        if (prior is null)
        {
            return;
        }

        var added = TierTraitBaseline.AddedEntries(TierTraitBaseline.ReadLines(), prior);
        added.Should().BeEmpty(
            "the tier-trait baseline may only shrink from the checked-out base; give every new " +
            "test a tier-bearing TestKit attribute instead of adding its name to the baseline. " +
            "Added entries:\n" + string.Join("\n", added));
    }

    [ArchitectureTest]
    public void TierTraitBaselineGrowth_RejectsNewFactEvenWhenItsNameIsAddedToTheBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"honua-tier-baseline-{Guid.NewGuid():N}");
        var path = Path.Combine(root, TierTraitBaseline.RelativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TierTraitBaseline.RunGit(root, "init", "-q");
            File.WriteAllText(Path.Combine(root, "README"), "initial\n");
            TierTraitBaseline.RunGit(root, "add", "README");
            TierTraitBaseline.RunGit(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "initial");

            File.WriteAllText(path, "Example.ExistingBareFact\n");
            TierTraitBaseline.RunGit(root, "add", TierTraitBaseline.RelativePath);
            TierTraitBaseline.RunGit(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "introduce-baseline");
            TierTraitBaseline.ReadFromFirstParent(root).Should().BeNull("the first baseline is allowed to be seeded");

            File.AppendAllText(path, "Example.NewBareFact\n");
            TierTraitBaseline.RunGit(root, "add", TierTraitBaseline.RelativePath);
            TierTraitBaseline.RunGit(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "add-untiered-test-and-baseline-line");

            var prior = TierTraitBaseline.ReadFromFirstParent(root)!;
            TierTraitBaseline.AddedEntries(File.ReadAllLines(path), prior)
                .Should().ContainSingle().Which.Should().Be("Example.NewBareFact");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Pins the tier-detection primitive itself.
    /// </summary>
    /// <remarks>
    /// The guard above is only as good as <see cref="TierTraitScanner.EmitsTierTrait"/>. If that
    /// silently stops recognising the TestKit attributes, EVERY method looks untiered: the emitter
    /// writes a baseline covering the whole suite (15 398 entries when this was first generated,
    /// against ~3 100 genuinely untiered methods), and afterwards the enforcement guard fails every
    /// NEWLY ADDED and correctly tiered test — the exact inverse of what it is for. That failure mode
    /// is invisible in the guard's own output, so it gets its own assertion.
    /// </remarks>
    [ArchitectureTest]
    public void TierDetection_RecognisesTestKitTierAttributes_AndRejectsPlainXunitFacts()
    {
        TierTraitScanner.EmitsTierTrait(typeof(UnitTestAttribute)).Should().BeTrue(
            "[UnitTest] emits Tier=Fast, which is what the required gate filters on");
        TierTraitScanner.EmitsTierTrait(typeof(UnitTheoryAttribute)).Should().BeTrue(
            "[UnitTheory] emits Tier=Fast for parameterized tests");
        TierTraitScanner.EmitsTierTrait(typeof(IntegrationTestAttribute)).Should().BeTrue(
            "[IntegrationTest] emits Tier=Integration");
        TierTraitScanner.EmitsTierTrait(typeof(ScaleTestAttribute)).Should().BeTrue(
            "[ScaleTest] emits Tier=Slow");

        TierTraitScanner.EmitsTierTrait(typeof(FactAttribute)).Should().BeFalse(
            "a plain [Fact] emits no trait at all — that is the hole this guard closes");
        TierTraitScanner.EmitsTierTrait(typeof(TheoryAttribute)).Should().BeFalse(
            "a plain [Theory] emits no trait at all");
        TierTraitScanner.EmitsTierTrait(typeof(ArchitectureTestAttribute)).Should().BeFalse(
            "[ArchitectureTest] emits Category=Architecture but no Tier, so it must not satisfy the guard");
    }

    [ArchitectureTest]
    public void ArchitectureCategoryDetection_ReadsXunitTraitConstructorArguments()
    {
        TierTraitScanner.HasArchitectureCategoryTrait(typeof(ArchitectureCategoryFixture)).Should().BeTrue();
        TierTraitScanner.HasArchitectureCategoryTrait(
            typeof(ArchitectureCategoryFixture).GetMethod(nameof(ArchitectureCategoryFixture.MarkedMethod))!)
            .Should().BeTrue();
        TierTraitScanner.HasArchitectureCategoryTrait(
            typeof(ArchitectureCategoryFixture).GetMethod(nameof(ArchitectureCategoryFixture.OtherMethod))!)
            .Should().BeFalse();
        TierTraitScanner.HasArchitectureCategoryTrait(typeof(TierTraitBaseline)).Should().BeFalse();
    }

    [ArchitectureTest]
    public void TierBaselineKeys_DistinguishUntieredMethodOverloads()
    {
        var overloads = typeof(OverloadedTestFixture).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == nameof(OverloadedTestFixture.BareTheory))
            .Select(method => TierTraitScanner.MethodKey(typeof(OverloadedTestFixture), method))
            .ToArray();

        overloads.Should().HaveCount(2);
        overloads.Should().OnlyHaveUniqueItems(
            "a new bare [Theory] overload must not reuse an existing baseline key");
        var existing = overloads.Single(key => key.EndsWith("BareTheory`0(System.Int32)", StringComparison.Ordinal));
        var added = overloads.Single(key => key.EndsWith("BareTheory`0(System.String)", StringComparison.Ordinal));

        overloads.Except([existing], StringComparer.Ordinal).Should().ContainSingle().Which.Should().Be(added,
            "baselining one overload must leave a newly untiered overload visible");
        TierTraitBaseline.AddedEntries([existing, added], [existing])
            .Should().ContainSingle().Which.Should().Be(added,
                "adding the new overload to the committed baseline must fail the growth guard");
        TierTraitBaseline.AddedEntries([added], [existing])
            .Should().ContainSingle().Which.Should().Be(added,
                "replacing the sole old signature must not grandfather a newly untiered method");
        var legacyKey = $"{typeof(OverloadedTestFixture).FullName}.BareTheory";
        TierTraitBaseline.AddedEntries([added], [legacyKey])
            .Should().ContainSingle().Which.Should().Be(added,
                "a historical name-only entry cannot authorize a new signature");
    }

    private sealed class OverloadedTestFixture
    {
        // xUnit's analyzer rejects decorated overloads in the fixture itself; the scanner's
        // baseline key uses the same MethodInfo signature for a decorated test method.
        public void BareTheory(int value)
        {
        }

        public void BareTheory(string value)
        {
        }
    }

    [Trait("Category", "Architecture")]
    private sealed class ArchitectureCategoryFixture
    {
        [Trait("Category", "Architecture")]
        public void MarkedMethod()
        {
        }

        [Trait("Category", "Other")]
        public void OtherMethod()
        {
        }
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
    /// Fully-qualified <c>Namespace.Type.Method(ParameterTypes)</c> signatures of every discovered test method that
    /// carries no tier-bearing attribute, sorted ordinally.
    /// </summary>
    internal static IReadOnlyCollection<string> UntieredTestMethodNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var assembly in ScannedAssemblies())
        {
            foreach (var type in ArchitectureTestHelpers.GetTypesSafely(assembly))
            {
                // Static classes hold no xUnit test methods; a null FullName would produce an
                // unusable baseline key, so both are skipped before any attribute is read.
                if (type is null || type.FullName is null || (type.IsAbstract && type.IsSealed))
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

                    names.Add(MethodKey(type, method));
                }
            }
        }

        return names;
    }

    internal static string MethodKey(Type type, MethodInfo method)
    {
        var genericArity = method.GetGenericArguments().Length;
        var parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.ToString()));
        return $"{type.FullName}.{method.Name}`{genericArity}({parameters})";
    }

    private static bool IsXunitTestMethod(MethodInfo method)
        => method.GetCustomAttributes(inherit: true)
            .Any(attribute => IsOrDerivesFromXunitFact(attribute.GetType()));

    private static bool IsOrDerivesFromXunitFact(Type attributeType)
    {
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            var fullName = current.FullName;
            if (fullName is not null && XunitFactAttributeNames.Contains(fullName, StringComparer.Ordinal))
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

    internal static bool EmitsTierTrait(Type attributeType)
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

    /// <summary>
    /// Resolves the <c>ITraitDiscoverer</c> that xUnit would use for <paramref name="attributeType"/>.
    /// </summary>
    /// <remarks>
    /// <c>Xunit.Sdk.TraitDiscovererAttribute</c> is declared with an EMPTY BODY in xunit.core: it
    /// stores its <c>typeName</c>/<c>assemblyName</c> constructor arguments nowhere readable, which
    /// is why xUnit's own discovery reads them through <c>IAttributeInfo.GetConstructorArguments()</c>.
    /// Reflecting for <c>TypeName</c>/<c>AssemblyName</c> PROPERTIES therefore always yields null and
    /// would make every attribute — including <c>[UnitTest]</c> and <c>[IntegrationTest]</c> — look
    /// non-tier-bearing, so the guard would baseline the entire suite and then fail every newly added
    /// and correctly tiered test. The arguments must come from the attribute METADATA instead.
    /// </remarks>
    private static Type? ResolveDiscovererType(Type attributeType)
    {
        // [TraitDiscoverer] is Inherited=true, so mirror xUnit and walk the attribute's base chain.
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            var data = current.GetCustomAttributesData().FirstOrDefault(candidate =>
                string.Equals(
                    candidate.AttributeType.FullName,
                    "Xunit.Sdk.TraitDiscovererAttribute",
                    StringComparison.Ordinal));

            if (data is null)
            {
                continue;
            }

            var arguments = data.ConstructorArguments;

            // [TraitDiscoverer(Type discovererType)]
            if (arguments.Count == 1 && arguments[0].Value is Type discovererType)
            {
                return discovererType;
            }

            // [TraitDiscoverer(string typeName, string assemblyName)] — the form TestKit uses.
            if (arguments.Count == 2 &&
                arguments[0].Value is string typeName &&
                arguments[1].Value is string assemblyName &&
                !string.IsNullOrWhiteSpace(typeName) &&
                !string.IsNullOrWhiteSpace(assemblyName))
            {
                try
                {
                    return Assembly.Load(assemblyName).GetType(typeName);
                }
                catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException or TypeLoadException)
                {
                    return null;
                }
            }

            return null;
        }

        return null;
    }

    private static bool ResolveEmitsTierTrait(Type attributeType)
    {
        var discovererType = ResolveDiscovererType(attributeType);
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

            // Enumerate INSIDE the try. GetTraits implementations are iterator methods, so a
            // discoverer that really does dereference its IAttributeInfo argument throws from
            // MoveNext() — not from Invoke() — and that exception is therefore NOT wrapped in
            // TargetInvocationException.
            foreach (var trait in traits)
            {
                var key = trait?.GetType().GetProperty("Key")?.GetValue(trait) as string;
                if (string.Equals(key, "Tier", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A discoverer that genuinely needs its argument cannot be probed this way; treat it
            // as non-tier-bearing rather than crashing the guard over an unrelated attribute.
            // This cannot silently swallow a REAL tier attribute: TierDetection_RecognisesTestKit
            // AttributesAndRejectsPlainXunitFacts pins [UnitTest]/[IntegrationTest]/[ScaleTest]
            // as tier-bearing, and fails loudly if this path ever starts absorbing one of them.
            return false;
        }

        return false;
    }

    internal static bool HasArchitectureCategoryTrait(MemberInfo member)
        => member.GetCustomAttributesData().Any(attribute =>
        {
            if (!string.Equals(attribute.AttributeType.FullName, "Xunit.TraitAttribute", StringComparison.Ordinal))
            {
                return false;
            }

            // xUnit's TraitAttribute stores these constructor arguments without exposing
            // Name/Value properties, just like its TraitDiscovererAttribute.
            var arguments = attribute.ConstructorArguments;
            var name = arguments.Count == 2 ? arguments[0].Value as string : null;
            var value = arguments.Count == 2 ? arguments[1].Value as string : null;
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

    public static IReadOnlyList<string> AddedEntries(IEnumerable<string> current, IEnumerable<string> prior)
        => current.Except(prior, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string>? ReadFromFirstParent(string repositoryRoot)
    {
        var parent = RunGit(repositoryRoot, "rev-parse", "--verify", "HEAD^1^{commit}").Trim();
        var path = RunGit(repositoryRoot, "ls-tree", "--name-only", parent, "--", RelativePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return RunGit(repositoryRoot, "show", $"{parent}:{RelativePath}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
    }

    public static string RunGit(string repositoryRoot, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git for tier baseline comparison.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output;
    }

    public static void Write(IReadOnlyCollection<string> names)
    {
        var ordered = names.OrderBy(name => name, StringComparer.Ordinal);
        File.WriteAllText(
            AbsolutePath(),
            Header + string.Join('\n', ordered) + (names.Count > 0 ? "\n" : string.Empty),
            System.Text.Encoding.UTF8);
    }
}
