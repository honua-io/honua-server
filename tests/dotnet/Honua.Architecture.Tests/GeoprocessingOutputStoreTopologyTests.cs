// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Anti-drift gate for referenced geoprocessing output staging topologies
/// (honua-io/honua-server#3900).
/// </summary>
/// <remarks>
/// <para>
/// The runtime fails closed when a host binds an unattested store, but nothing
/// stopped a checked-in topology from enabling staging without a persistence
/// and backup contract — which is how the qualification harness for #3852 came
/// to enable staging against a bare container path. These tests make the
/// denominator mechanical: every deployment topology in the repository that
/// enables staging must bind a complete store contract on every producer and
/// consumer, and its declared digest is recomputed here from that topology's
/// own values rather than trusted.
/// </para>
/// <para>
/// The denominator is the whole repository, not <c>docker/</c>: any deployment
/// manifest that binds the staging section is discovered wherever it lives. The
/// manifests are parsed rather than evaluated, so a topology shape this parser
/// does not understand fails the suite instead of skipping the check.
/// </para>
/// </remarks>
public sealed class GeoprocessingOutputStoreTopologyTests : IDisposable
{
    private const string Prefix = "Geoprocessing__OutputStaging__";

    /// <summary>
    /// Directories that are not part of the repository's deployment surface: build
    /// output, package caches, editor state and tool artifacts.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "artifacts", "TestResults",
    };

    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-topology-").FullName;

    /// <summary>Settings a staging-enabled host must bind for the runtime to attest its store.</summary>
    private static readonly string[] RequiredSettings =
    [
        "Provider", "StoreReference", "LocalRootPath", "KeyPrefix", "PersistenceClass",
        "BackupIdentity", "BackupStoreReferences__0", "ConfigurationDigest",
        "MaxInlineArtifactBytes", "ReadLeaseDuration", "SweepInterval", "SweepGrace", "OrphanRetention",
    ];

    [Fact]
    [Trait("Category", "Architecture")]
    public void StagingEnabledTopologies_BindAnAttestedStoreOnEveryProducerAndConsumer()
    {
        var topologies = LoadTopologies();
        topologies.Should().NotBeEmpty("referenced output staging must be exercised by at least one topology");

        AssertAttestedStores(topologies);
    }

    /// <summary>
    /// The contract every staging-enabled host must declare, shared by the repository
    /// scan and the discovery fixtures so both hold the topologies to one rule.
    /// </summary>
    private static void AssertAttestedStores(
        IEnumerable<(string File, string Service, IReadOnlyDictionary<string, string> Settings)> topologies)
    {
        foreach (var (file, service, settings) in topologies)
        {
            var missing = RequiredSettings.Where(setting => !settings.ContainsKey(setting)).ToArray();
            missing.Should().BeEmpty($"{file}:{service} enables referenced output staging, so it must declare "
                + "a store reference, persistence class and backup identity rather than a bare container path");
            settings["PersistenceClass"].Should().Be("shared-persistent", $"{file}:{service}");
            // A backup identity may cover several stores, in any order; the runtime
            // only requires that the staging store be one of them.
            BackupStoreReferences(settings).Should().Contain(settings["StoreReference"],
                $"{file}:{service} must place its staging store inside the declared backup set");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void StagingEnabledTopologies_DeclareTheDigestTheRuntimeRecomputes()
    {
        foreach (var (file, service, settings) in LoadTopologies())
        {
            var options = ToOptions(settings);
            GeoprocessingOutputStoreAttestation.Create(options).ConfigurationDigest.Should()
                .Be(settings["ConfigurationDigest"],
                    $"{file}:{service} declares a configuration digest that the runtime would reject at startup");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void StagingEnabledTopologies_ResolveOneStoreIdentityPerFile()
    {
        foreach (var group in LoadTopologies().GroupBy(topology => topology.File, StringComparer.Ordinal))
        {
            // Producers and consumers sharing a volume must agree on everything the
            // digest covers; only the mount path may differ between hosts.
            group.Should().HaveCountGreaterThan(1, $"{group.Key} must bind both a server and a worker");
            group.Select(topology => topology.Settings["ConfigurationDigest"]).Distinct(StringComparer.Ordinal)
                .Should().ContainSingle($"{group.Key} binds more than one store identity across its hosts");
        }
    }

    /// <summary>
    /// Pins the #3852 qualification store to a digest computed by hand from the
    /// versioned canonical form rather than from a run of
    /// <see cref="GeoprocessingOutputStoreAttestation.Create"/>, so the runtime and
    /// the deployment provisioning scripts cannot drift together.
    /// </summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void QualificationTopology_MatchesTheIndependentlyComputedCanonicalDigest()
    {
        var qualification = LoadTopologies()
            .Where(topology => topology.Settings["StoreReference"] == "qualification")
            .ToArray();
        qualification.Should().NotBeEmpty();
        foreach (var topology in qualification)
        {
            topology.Settings["ConfigurationDigest"].Should()
                .Be("bb6a13a6b7970d85b518145569fd6470eb6940524ced24fa0994fa1d391f4041");
        }
    }

    /// <summary>
    /// The gate's denominator is the repository, not <c>docker/</c>. A staging-enabled
    /// topology shipped from any other directory — a Helm chart, a Kubernetes overlay, a
    /// second Compose lane — must be held to the same store contract, and one that binds
    /// a bare container path must fail here rather than at a customer's first restart.
    /// </summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void Denominator_UnattestedTopologyOutsideDocker_IsCaught()
    {
        var root = CreateTree(
            ("docker/gp-reliability/compose.yml", ComposeTopology(TestContract())),
            ("deploy/helm/templates/worker.yaml", BareContainerPathTopology()));

        DiscoverStagingManifests(root).Should().Equal(
            "deploy/helm/templates/worker.yaml", "docker/gp-reliability/compose.yml");

        var act = () => AssertAttestedStores(LoadTopologies(root));
        act.Should().Throw<Xunit.Sdk.XunitException>()
            .WithMessage("*deploy/helm/templates/worker.yaml*");
    }

    /// <summary>
    /// The same walk must accept a fully attested topology outside <c>docker/</c>,
    /// including recomputing its declared digest, so widening the denominator adds
    /// coverage rather than a location rule.
    /// </summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void Denominator_AttestedTopologyOutsideDocker_ResolvesEveryHost()
    {
        var root = CreateTree(("deploy/compose.gp.yaml", ComposeTopology(TestContract())));

        var topologies = LoadTopologies(root);

        topologies.Select(topology => topology.Service).Should().Equal("server", "worker");
        topologies.Select(topology => topology.File).Distinct(StringComparer.Ordinal)
            .Should().Equal("deploy/compose.gp.yaml");
        AssertAttestedStores(topologies);
        foreach (var topology in topologies)
        {
            GeoprocessingOutputStoreAttestation.Create(ToOptions(topology.Settings)).ConfigurationDigest
                .Should().Be(topology.Settings["ConfigurationDigest"]);
        }
    }

    /// <summary>
    /// A deployment manifest binding the staging section in a shape this gate cannot
    /// resolve — a Kubernetes <c>env</c> list, or the colon spelling in an
    /// <c>appsettings</c> override — must fail the suite. Silently skipping it would
    /// restore the hole the repository-wide walk exists to close.
    /// </summary>
    /// <param name="path">Manifest path relative to the constructed repository root.</param>
    /// <param name="content">Manifest content binding the staging section.</param>
    [Theory]
    [Trait("Category", "Architecture")]
    [InlineData("deploy/k8s/worker.yaml", """
        spec:
          containers:
            - name: worker
              env:
                - name: Geoprocessing__OutputStaging__Enabled
                  value: "true"
                - name: Geoprocessing__OutputStaging__LocalRootPath
                  value: /var/lib/honua/gp-outputs
        """)]
    [InlineData("deploy/appsettings.Production.json", """
        {
          "Geoprocessing": {
            "OutputStaging": {
              "Enabled": true,
              "LocalRootPath": "/var/lib/honua/gp-outputs"
            }
          }
        }
        """)]
    [InlineData("deploy/gp.env", "Geoprocessing:OutputStaging:Enabled=true\n")]
    public void Denominator_ManifestShapeThisGateCannotParse_FailsClosed(string path, string content)
    {
        var root = CreateTree((path, content));

        DiscoverStagingManifests(root).Should().Equal(path);

        var act = () => LoadTopologies(root);
        act.Should().Throw<Xunit.Sdk.XunitException>().WithMessage("*teach this gate the new topology shape*");
    }

    /// <summary>
    /// Build output, package caches and nested checkouts are not this repository's
    /// deployment surface. A lane worktree dropped inside the tree carries a whole other
    /// branch's manifests; letting the walk descend into it would make this gate's
    /// verdict depend on which worktrees happen to exist on the machine running it.
    /// </summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void Denominator_SkipsBuildOutputAndNestedCheckouts()
    {
        var unattested = BareContainerPathTopology();
        var root = CreateTree(
            ("src/Honua.Server/obj/compose.yml", unattested),
            ("src/Honua.Server/bin/Debug/compose.yml", unattested),
            ("tests/js/node_modules/pkg/compose.yml", unattested),
            ("artifacts/publish/compose.yml", unattested),
            (".worktrees/lane/.git", "gitdir: /somewhere/else\n"),
            (".worktrees/lane/docker/compose.yml", unattested));

        DiscoverStagingManifests(root).Should().BeEmpty();
    }

    /// <summary>
    /// A manifest the repository does not ship — a developer's own ignored env file —
    /// must not decide this gate. Otherwise the suite passes or fails on workstation
    /// state, and the one shape that is both commonly local and unparsable here
    /// (<c>.env.local</c>) would fail it for everyone who has one.
    /// </summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void Denominator_GitIgnoredManifest_IsNotScanned()
    {
        var root = CreateTree(
            (".gitignore", "*.local\n"),
            ("deploy/gp.env.local", "Geoprocessing__OutputStaging__Enabled=true\n"),
            ("deploy/compose.yml", ComposeTopology(TestContract())));
        Git(root, "init --quiet");

        DiscoverStagingManifests(root).Should().Equal("deploy/compose.yml");
        AssertAttestedStores(LoadTopologies(root));
    }

    /// <summary>
    /// Configuration keys are case-insensitive, so a topology spelling them in lower case
    /// stages output exactly like the canonical one. It must be discovered and held to the
    /// same contract, not quietly dropped from the denominator.
    /// </summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void Denominator_LowercaseConfigurationKeys_AreHeldToTheSameContract()
    {
        var lowercase = ComposeTopology(TestContract())
            .Replace(Prefix, Prefix.ToLowerInvariant(), StringComparison.Ordinal);
        var root = CreateTree(("deploy/compose.yml", lowercase));

        var topologies = LoadTopologies(root);

        topologies.Select(topology => topology.Service).Should().Equal("server", "worker");
        AssertAttestedStores(topologies);
        foreach (var topology in topologies)
        {
            GeoprocessingOutputStoreAttestation.Create(ToOptions(topology.Settings)).ConfigurationDigest
                .Should().Be(topology.Settings["ConfigurationDigest"]);
        }
    }

    /// <summary>Runs a Git command in the constructed tree, failing the test if it errors.</summary>
    private static void Git(string root, string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git")
        {
            Arguments = arguments,
            WorkingDirectory = root,
            RedirectStandardError = true,
        })!;
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        git.ExitCode.Should().Be(0, error);
    }

    /// <summary>An attested contract for the discovery fixtures.</summary>
    private static GeoprocessingOutputStagingOptions TestContract()
        => new()
        {
            Enabled = true,
            StoreReference = "elsewhere",
            PersistenceClass = "shared-persistent",
            BackupIdentity = "elsewhere-backup",
            BackupStoreReferences = ["elsewhere"],
            MaxInlineArtifactBytes = 1024,
        };

    /// <summary>Writes the given files under a fresh temporary repository root.</summary>
    private string CreateTree(params (string Path, string Content)[] files)
    {
        var root = Path.Join(_root, Guid.NewGuid().ToString("n"));
        foreach (var (path, content) in files)
        {
            var target = Path.Join(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        return root;
    }

    /// <summary>
    /// A Compose topology binding <paramref name="options"/> on a server and a worker,
    /// carrying the digest the runtime recomputes from those same values.
    /// </summary>
    private static string ComposeTopology(GeoprocessingOutputStagingOptions options)
    {
        var settings = string.Join('\n', new[]
        {
            $"      {Prefix}Enabled: \"true\"",
            $"      {Prefix}Provider: {options.Provider}",
            $"      {Prefix}StoreReference: {options.StoreReference}",
            $"      {Prefix}LocalRootPath: /var/lib/honua/gp-outputs",
            $"      {Prefix}KeyPrefix: {options.KeyPrefix}",
            $"      {Prefix}PersistenceClass: {options.PersistenceClass}",
            $"      {Prefix}BackupIdentity: {options.BackupIdentity}",
            $"      {Prefix}BackupStoreReferences__0: {options.BackupStoreReferences[0]}",
            $"      {Prefix}ConfigurationDigest: {GeoprocessingOutputStoreAttestation.Create(options).ConfigurationDigest}",
            $"      {Prefix}MaxInlineArtifactBytes: \"{options.MaxInlineArtifactBytes.ToString(CultureInfo.InvariantCulture)}\"",
            $"      {Prefix}ReadLeaseDuration: {options.ReadLeaseDuration.ToString(null, CultureInfo.InvariantCulture)}",
            $"      {Prefix}SweepInterval: {options.SweepInterval.ToString(null, CultureInfo.InvariantCulture)}",
            $"      {Prefix}SweepGrace: {options.SweepGrace.ToString(null, CultureInfo.InvariantCulture)}",
            $"      {Prefix}OrphanRetention: {options.OrphanRetention.ToString(null, CultureInfo.InvariantCulture)}",
        });

        return $"services:\n  server:\n    environment: &shared\n{settings}\n\n  worker:\n    environment: *shared\n";
    }

    /// <summary>The defect this gate exists for: staging enabled against a bare container path.</summary>
    private static string BareContainerPathTopology()
        => "services:\n  worker:\n    environment:\n"
            + $"      {Prefix}Enabled: \"true\"\n"
            + $"      {Prefix}Provider: local\n"
            + $"      {Prefix}LocalRootPath: /var/lib/honua/gp-outputs\n";

    /// <summary>
    /// The bound configuration array, in the index order the options binder uses:
    /// the inventory is order-sensitive to the digest only after the runtime sorts
    /// it, so the order here must be the topology's, not a convenient one.
    /// </summary>
    private static string[] BackupStoreReferences(IReadOnlyDictionary<string, string> settings)
        => settings
            .Where(setting => setting.Key.StartsWith("BackupStoreReferences__", StringComparison.OrdinalIgnoreCase))
            .OrderBy(setting => int.Parse(setting.Key["BackupStoreReferences__".Length..], CultureInfo.InvariantCulture))
            .Select(setting => setting.Value)
            .ToArray();

    private static GeoprocessingOutputStagingOptions ToOptions(IReadOnlyDictionary<string, string> settings)
        => new()
        {
            Enabled = true,
            Provider = settings["Provider"],
            StoreReference = settings["StoreReference"],
            LocalRootPath = settings["LocalRootPath"],
            KeyPrefix = settings["KeyPrefix"],
            PersistenceClass = settings["PersistenceClass"],
            BackupIdentity = settings["BackupIdentity"],
            BackupStoreReferences = BackupStoreReferences(settings),
            MaxInlineArtifactBytes = int.Parse(settings["MaxInlineArtifactBytes"], CultureInfo.InvariantCulture),
            ReadLeaseDuration = TimeSpan.Parse(settings["ReadLeaseDuration"], CultureInfo.InvariantCulture),
            SweepInterval = TimeSpan.Parse(settings["SweepInterval"], CultureInfo.InvariantCulture),
            SweepGrace = TimeSpan.Parse(settings["SweepGrace"], CultureInfo.InvariantCulture),
            OrphanRetention = TimeSpan.Parse(settings["OrphanRetention"], CultureInfo.InvariantCulture),
        };

    private static List<(string File, string Service, IReadOnlyDictionary<string, string> Settings)> LoadTopologies()
        => LoadTopologies(ArchitectureTestHelpers.ResolveRepositoryRoot());

    /// <summary>
    /// Resolves every staging-enabled host in every deployment manifest the repository
    /// ships, rooted at <paramref name="root"/> so the discovery rules themselves can be
    /// exercised against a constructed tree.
    /// </summary>
    internal static List<(string File, string Service, IReadOnlyDictionary<string, string> Settings)> LoadTopologies(
        string root)
    {
        var results = new List<(string, string, IReadOnlyDictionary<string, string>)>();

        foreach (var relative in DiscoverStagingManifests(root))
        {
            var text = File.ReadAllText(Path.Join(root, relative));
            var enabled = ParseServices(relative, text)
                .Where(service => service.Settings.TryGetValue("Enabled", out var value)
                    && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            enabled.Should().NotBeEmpty($"{relative} configures referenced output staging but no service could be "
                + "parsed as enabling it; teach this gate the new topology shape rather than leaving it unchecked");
            results.AddRange(enabled.Select(service => (relative, service.Service, service.Settings)));
        }

        return results;
    }

    /// <summary>
    /// Returns the repository-relative path of every deployment manifest that mentions
    /// the referenced-output staging section, in any spelling the .NET configuration
    /// binder accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The denominator this issue names is <em>every topology that enables referenced
    /// output staging</em>, so the walk starts at the repository root rather than at
    /// <c>docker/</c>. A Compose file, Kubernetes manifest, Helm template, env file or
    /// <c>appsettings</c> override added under any other directory would otherwise enable
    /// staging against a bare container path with no gate noticing — the exact miss that
    /// left the #3852 qualification topology unstartable until #4510.
    /// </para>
    /// <para>
    /// Discovery deliberately over-collects: a manifest matched here whose shape
    /// <see cref="ParseServices"/> cannot resolve fails
    /// <see cref="LoadTopologies(string)"/> rather than being skipped, so extending the
    /// deployment surface forces this gate to be extended with it.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> DiscoverStagingManifests(string root)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                // Build output, package caches and nested checkouts (a lane worktree
                // dropped inside the tree carries its own .git) are not this
                // repository's deployment surface, and walking them would let another
                // branch's manifests decide this gate's verdict.
                if (ExcludedDirectories.Contains(Path.GetFileName(child))
                    || Directory.Exists(Path.Join(child, ".git"))
                    || File.Exists(Path.Join(child, ".git")))
                {
                    continue;
                }

                pending.Push(child);
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (!IsDeploymentManifest(Path.GetFileName(path)) || !ConfiguresStaging(File.ReadAllText(path)))
                {
                    continue;
                }

                results.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }

        results.RemoveAll(GitIgnoredPaths(root, results).Contains);
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    /// <summary>
    /// Returns the subset of <paramref name="candidates"/> that Git ignores under
    /// <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// The gate must judge what the repository ships, not what a workstation happens to
    /// hold. A developer's own <c>.env.local</c> is exactly the kind of file that both
    /// mentions the staging section and cannot be parsed as a topology, so without this
    /// the suite's verdict would depend on the machine running it. Git decides, so the
    /// answer matches <c>.gitignore</c> exactly rather than a second guess at it. A root
    /// that is not a Git work tree — the constructed fixtures — ignores nothing.
    /// </remarks>
    private static HashSet<string> GitIgnoredPaths(string root, List<string> candidates)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        // A linked worktree records .git as a file rather than a directory.
        if (candidates.Count == 0
            || (!Directory.Exists(Path.Join(root, ".git")) && !File.Exists(Path.Join(root, ".git"))))
        {
            return ignored;
        }

        using var git = Process.Start(new ProcessStartInfo("git")
        {
            // Without --no-index, check-ignore never reports a tracked path, so a file
            // the repository actually ships stays in the denominator whatever the ignore
            // rules say; only untracked, ignored files are dropped.
            Arguments = "check-ignore --stdin",
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;

        foreach (var candidate in candidates)
        {
            git.StandardInput.WriteLine(candidate);
        }

        git.StandardInput.Close();
        while (git.StandardOutput.ReadLine() is { } line)
        {
            ignored.Add(line.Replace('\\', '/').Trim());
        }

        git.WaitForExit();
        // 0 = some ignored, 1 = none ignored. Anything else means git could not answer,
        // and silently dropping nothing is the safe direction: the manifest stays in the
        // denominator and the gate still has to understand it.
        return git.ExitCode is 0 or 1 ? ignored : [];
    }

    /// <summary>File kinds that can carry deployment configuration for a host.</summary>
    private static bool IsDeploymentManifest(string name)
        => name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the text binds the staging section under any spelling the configuration
    /// binder resolves: the double-underscore environment form, the colon form used by
    /// command-line and in-memory sources, or a nested JSON section. Matching is
    /// case-insensitive because configuration keys are: a host reading
    /// <c>geoprocessing__outputstaging__enabled=true</c> stages output exactly as one
    /// reading the canonical casing, so an ordinal match here would let that topology
    /// out of the denominator entirely.
    /// </summary>
    private static bool ConfiguresStaging(string text)
        => text.Contains(Prefix, StringComparison.OrdinalIgnoreCase)
            || text.Contains(GeoprocessingOutputStagingOptions.SectionName, StringComparison.OrdinalIgnoreCase)
            || (text.Contains("\"Geoprocessing\"", StringComparison.OrdinalIgnoreCase)
                && text.Contains("\"OutputStaging\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Extracts the staging settings each Compose service resolves, following the
    /// anchor/alias reuse the qualification topology uses to keep its server
    /// replicas identical.
    /// </summary>
    private static List<(string Service, IReadOnlyDictionary<string, string> Settings)> ParseServices(
        string file, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var anchors = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        // YAML anchor names are case-sensitive; the staging setting keys under them are not.
        var services = new List<(string, IReadOnlyDictionary<string, string>)>();
        var aliases = new List<(string Service, string Anchor)>();
        var service = string.Empty;
        Dictionary<string, string>? current = null;

        foreach (var line in lines)
        {
            var serviceMatch = Regex.Match(line, "^  ([A-Za-z0-9._-]+):\\s*$");
            if (serviceMatch.Success)
            {
                service = serviceMatch.Groups[1].Value;
                current = null;
                continue;
            }

            var environmentMatch = Regex.Match(line, "^    environment:\\s*(?:(&|\\*)([A-Za-z0-9._-]+))?\\s*$");
            if (environmentMatch.Success && service.Length > 0)
            {
                current = null;
                if (environmentMatch.Groups[1].Value == "*")
                {
                    aliases.Add((service, environmentMatch.Groups[2].Value));
                    continue;
                }

                // Configuration keys are case-insensitive, so the resolved settings are
                // looked up that way too; otherwise a lowercase manifest would parse into
                // a dictionary none of the contract assertions could read.
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                services.Add((service, current));
                if (environmentMatch.Groups[1].Value == "&")
                {
                    anchors[environmentMatch.Groups[2].Value] = current;
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.Length > 0 && !line.StartsWith("      ", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            var settingMatch = Regex.Match(
                line,
                "^      " + Prefix + "([A-Za-z0-9_]+):\\s*\"?([^\"]*?)\"?\\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (settingMatch.Success)
            {
                current[settingMatch.Groups[1].Value] = settingMatch.Groups[2].Value;
            }
        }

        foreach (var (aliasService, anchor) in aliases)
        {
            anchors.Should().ContainKey(anchor,
                $"{file}:{aliasService} reuses an environment anchor this gate could not resolve");
            services.Add((aliasService, anchors[anchor]));
        }

        return services;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
