// Copyright 2025 Honua Authors
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class GeoprocessingOutputStoreTopologyTests : IDisposable
{
    private const string Prefix = "Geoprocessing__OutputStaging__";

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "artifacts", "TestResults",
    };

    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-topology-").FullName;

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

    private static string BareContainerPathTopology()
        => "services:\n  worker:\n    environment:\n"
            + $"      {Prefix}Enabled: \"true\"\n"
            + $"      {Prefix}Provider: local\n"
            + $"      {Prefix}LocalRootPath: /var/lib/honua/gp-outputs\n";

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

    private static bool IsDeploymentManifest(string name)
        => name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static bool ConfiguresStaging(string text)
        => text.Contains(Prefix, StringComparison.OrdinalIgnoreCase)
            || text.Contains(GeoprocessingOutputStagingOptions.SectionName, StringComparison.OrdinalIgnoreCase)
            || (text.Contains("\"Geoprocessing\"", StringComparison.OrdinalIgnoreCase)
                && text.Contains("\"OutputStaging\"", StringComparison.OrdinalIgnoreCase));

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
