// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// The Compose files are parsed rather than evaluated, so a topology shape this
/// parser does not understand fails the suite instead of skipping the check.
/// </para>
/// </remarks>
public sealed class GeoprocessingOutputStoreTopologyTests
{
    private const string Prefix = "Geoprocessing__OutputStaging__";

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
    /// The bound configuration array, in the index order the options binder uses:
    /// the inventory is order-sensitive to the digest only after the runtime sorts
    /// it, so the order here must be the topology's, not a convenient one.
    /// </summary>
    private static string[] BackupStoreReferences(IReadOnlyDictionary<string, string> settings)
        => settings
            .Where(setting => setting.Key.StartsWith("BackupStoreReferences__", StringComparison.Ordinal))
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
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var docker = Path.Join(root, "docker");
        var results = new List<(string, string, IReadOnlyDictionary<string, string>)>();
        var files = Directory.EnumerateFiles(docker, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(docker, "*.yaml", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            if (!text.Contains(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
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
    /// Extracts the staging settings each Compose service resolves, following the
    /// anchor/alias reuse the qualification topology uses to keep its server
    /// replicas identical.
    /// </summary>
    private static List<(string Service, IReadOnlyDictionary<string, string> Settings)> ParseServices(
        string file, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var anchors = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
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

                current = [];
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

            var settingMatch = Regex.Match(line, "^      " + Prefix + "([A-Za-z0-9_]+):\\s*\"?([^\"]*?)\"?\\s*$");
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
}
