// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.FileStorage;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Conformance gate for the POSIX store provisioner (honua-io/honua-server#3900).
/// </summary>
/// <remarks>
/// <para>
/// A referenced-output volume only becomes attested when a deployment runs
/// <c>scripts/operations/initialize-gp-output-store.sh</c> (or its PowerShell sibling)
/// to write the marker the runtime validates. The script re-implements the versioned
/// canonical form and SHA-256 digest of
/// <see cref="GeoprocessingOutputStoreAttestation.Create"/> in Bash — its own ordinal
/// inventory sort and its own <c>[d.]hh:mm:ss</c> to ticks conversion — so the two can
/// drift silently. When they do, every deployment provisioned by the documented
/// procedure fails closed at startup, and the failure reads as a broken mount rather
/// than a broken tool. The sibling <see cref="GeoprocessingOutputStoreAttestationTests"/>
/// pins the PowerShell implementation to a hand-carried digest; nothing covered the
/// POSIX one, which is the path every containerized topology uses.
/// </para>
/// <para>
/// The first vector's expected digest is not read back from the script or from the
/// runtime: it is the value the PowerShell provisioner independently produced for the
/// same contract, already pinned in the sibling suite. The second vector is the
/// checked-in qualification topology, which closes the loop between the manifest, the
/// provisioning procedure and the runtime validator.
/// </para>
/// </remarks>
public sealed class GeoprocessingOutputStoreProvisionerTests : IDisposable
{
    private const string ProvisionerRelativePath = "scripts/operations/initialize-gp-output-store.sh";

    /// <summary>
    /// The digest the PowerShell provisioner emits for the default contract over store
    /// <c>gp-outputs</c> with a 1 KiB inline ceiling, pinned independently of this suite in
    /// <c>GeoprocessingOutputStoreAttestationTests.Create_DeploymentToolDigest_MatchesIndependentPowerShellVector</c>.
    /// </summary>
    private const string IndependentDefaultContractDigest =
        "6eb07467421c0a70d34ef40a20aeb7f0767def7ba74cddb8b0c01d62db5b6103";

    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-provisioner-").FullName;

    [UnitTest]
    public async Task Provisioner_DefaultContract_WritesTheIndependentlyPinnedAttestation()
    {
        var root = CreateVolume("default-contract");

        var run = await RunProvisionerAsync(
            "--root-path", root,
            "--store-reference", "gp-outputs",
            "--persistence-class", "shared-persistent",
            "--backup-identity", "qualification-backup",
            "--backup-store-references", "gp-outputs",
            "--max-inline-artifact-bytes", "1024");

        run.ExitCode.Should().Be(0, run.StandardError);
        run.StandardOutput.Trim().Should().Be(IndependentDefaultContractDigest);

        // The marker the runtime reads must be the complete, credential-free record —
        // provider, store reference, digest, persistence class and backup identity — and
        // must carry no mount path or other deployment secret.
        ReadMarker(root).Should().Be(new GeoprocessingOutputStoreAttestation(
            "local", "gp-outputs", IndependentDefaultContractDigest, "shared-persistent", "qualification-backup"));
        File.ReadAllText(Path.Join(root, GeoprocessingOutputStoreAttestation.FileName))
            .Should().NotContain(root);
    }

    /// <summary>
    /// The provisioner's own ordinal sort and tick conversion have to agree with the
    /// runtime for a contract that actually exercises them: a multi-store backup inventory
    /// whose ordinal order differs from a locale-aware one, and non-default sweep/retention
    /// durations including a day component.
    /// </summary>
    [UnitTest]
    public async Task Provisioner_UnsortedInventoryAndTunedDurations_MatchesTheRuntimeDigest()
    {
        var root = CreateVolume("tuned-contract");
        var options = new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            StoreReference = "gp-outputs",
            PersistenceClass = "shared-persistent",
            BackupIdentity = "nightly-snapshot",
            // Ordinal order is "B-archive", "a-tiles", "gp-outputs"; a locale-aware sort
            // would put "a-tiles" first and produce a digest no host could match.
            BackupStoreReferences = ["gp-outputs", "a-tiles", "B-archive"],
            KeyPrefix = "gp/outputs/v2",
            MaxInlineArtifactBytes = 2048,
            ReadLeaseDuration = TimeSpan.FromMinutes(20),
            SweepInterval = TimeSpan.FromSeconds(90),
            SweepGrace = TimeSpan.FromHours(2),
            OrphanRetention = TimeSpan.FromDays(3) + TimeSpan.FromHours(4),
        };

        var run = await RunProvisionerAsync(
            "--root-path", root,
            "--store-reference", options.StoreReference,
            "--persistence-class", options.PersistenceClass!,
            "--backup-identity", options.BackupIdentity!,
            "--backup-store-references", string.Join(',', options.BackupStoreReferences),
            "--key-prefix", options.KeyPrefix,
            "--max-inline-artifact-bytes", options.MaxInlineArtifactBytes.ToString(CultureInfo.InvariantCulture),
            "--read-lease-duration", "00:20:00",
            "--sweep-interval", "00:01:30",
            "--sweep-grace", "02:00:00",
            "--orphan-retention", "3.04:00:00");

        run.ExitCode.Should().Be(0, run.StandardError);
        var expected = GeoprocessingOutputStoreAttestation.Create(options);
        run.StandardOutput.Trim().Should().Be(expected.ConfigurationDigest);
        ReadMarker(root).Should().Be(expected);

        // And the runtime accepts the volume the script just provisioned.
        options.LocalRootPath = root;
        options.ConfigurationDigest = expected.ConfigurationDigest;
        GeoprocessingOutputStoreAttestationValidator.IsValid(options).Should().BeTrue();
    }

    /// <summary>
    /// The documented procedure must provision a store the checked-in qualification
    /// topology accepts: the digest the compose file declares is the digest the script
    /// produces from that topology's own settings.
    /// </summary>
    [UnitTest]
    public async Task Provisioner_QualificationTopologySettings_ReproduceTheDeclaredDigest()
    {
        var topology = ReadQualificationTopology();
        var root = CreateVolume("qualification-topology");

        var run = await RunProvisionerAsync(
            "--root-path", root,
            "--store-reference", topology["StoreReference"],
            "--persistence-class", topology["PersistenceClass"],
            "--backup-identity", topology["BackupIdentity"],
            "--backup-store-references", topology["BackupStoreReferences__0"],
            "--key-prefix", topology["KeyPrefix"],
            "--max-inline-artifact-bytes", topology["MaxInlineArtifactBytes"],
            "--read-lease-duration", topology["ReadLeaseDuration"],
            "--sweep-interval", topology["SweepInterval"],
            "--sweep-grace", topology["SweepGrace"],
            "--orphan-retention", topology["OrphanRetention"]);

        run.ExitCode.Should().Be(0, run.StandardError);
        run.StandardOutput.Trim().Should().Be(topology["ConfigurationDigest"],
            "the documented provisioning procedure must attest a store the checked-in topology binds");
        ReadMarker(root).ConfigurationDigest.Should().Be(topology["ConfigurationDigest"]);
    }

    /// <summary>
    /// Re-provisioning in place would silently re-attest a different store or backup
    /// policy over live data, so it must fail and leave the existing marker untouched.
    /// </summary>
    [UnitTest]
    public async Task Provisioner_AlreadyAttestedRoot_RefusesAndPreservesTheMarker()
    {
        var root = CreateVolume("already-attested");
        var first = await RunProvisionerAsync(
            "--root-path", root,
            "--store-reference", "gp-outputs",
            "--persistence-class", "shared-persistent",
            "--backup-identity", "qualification-backup",
            "--backup-store-references", "gp-outputs",
            "--max-inline-artifact-bytes", "1024");
        first.ExitCode.Should().Be(0, first.StandardError);
        var provisioned = File.ReadAllBytes(Path.Join(root, GeoprocessingOutputStoreAttestation.FileName));

        var second = await RunProvisionerAsync(
            "--root-path", root,
            "--store-reference", "other-store",
            "--persistence-class", "shared-persistent",
            "--backup-identity", "other-backup",
            "--backup-store-references", "other-store",
            "--max-inline-artifact-bytes", "1024");

        second.ExitCode.Should().NotBe(0);
        File.ReadAllBytes(Path.Join(root, GeoprocessingOutputStoreAttestation.FileName))
            .Should().Equal(provisioned);
    }

    /// <summary>
    /// A store outside the declared backup inventory is exactly the failure #3900 is
    /// about — bytes that survive nothing. The script must refuse it before any marker
    /// exists, matching the runtime's own precondition.
    /// </summary>
    [UnitTest]
    public async Task Provisioner_StoreOutsideTheBackupInventory_RefusesAndWritesNoMarker()
    {
        var root = CreateVolume("outside-backup-set");

        var run = await RunProvisionerAsync(
            "--root-path", root,
            "--store-reference", "gp-outputs",
            "--persistence-class", "shared-persistent",
            "--backup-identity", "nightly-snapshot",
            "--backup-store-references", "unrelated-store");

        run.ExitCode.Should().NotBe(0);
        File.Exists(Path.Join(root, GeoprocessingOutputStoreAttestation.FileName)).Should().BeFalse();

        var refuse = () => GeoprocessingOutputStoreAttestation.Create(new GeoprocessingOutputStagingOptions
        {
            StoreReference = "gp-outputs",
            PersistenceClass = "shared-persistent",
            BackupIdentity = "nightly-snapshot",
            BackupStoreReferences = ["unrelated-store"],
        });
        refuse.Should().Throw<InvalidOperationException>("the runtime rejects the same contract the script refuses");
    }

    private string CreateVolume(string name)
    {
        var path = Path.Join(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static GeoprocessingOutputStoreAttestation ReadMarker(string root)
    {
        var marker = File.ReadAllText(Path.Join(root, GeoprocessingOutputStoreAttestation.FileName));
        return JsonSerializer.Deserialize<GeoprocessingOutputStoreAttestation>(marker)
            ?? throw new InvalidOperationException("The provisioner wrote an unreadable attestation marker.");
    }

    /// <summary>
    /// The staging settings the qualification Compose topology binds. Every host in that
    /// file must resolve one store identity — the architecture suite owns that invariant —
    /// so a key with two values here is a topology this gate must not silently average.
    /// </summary>
    private static Dictionary<string, string> ReadQualificationTopology()
    {
        const string Prefix = "Geoprocessing__OutputStaging__";
        var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(RepositoryPaths.Resolve("docker", "gp-reliability", "compose.yml")))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
            separator.Should().BeGreaterThan(0, trimmed);
            var key = trimmed[Prefix.Length..separator];
            var value = trimmed[(separator + 1)..].Trim().Trim('"');
            if (!settings.TryGetValue(key, out var values))
            {
                values = [];
                settings[key] = values;
            }

            if (!values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }

        settings.Should().NotBeEmpty("the qualification topology must still enable referenced output staging");
        foreach (var (key, values) in settings)
        {
            values.Should().ContainSingle($"the qualification topology binds more than one {key} across its hosts");
        }

        return settings.ToDictionary(setting => setting.Key, setting => setting.Value[0], StringComparer.Ordinal);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProvisionerAsync(
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(RepositoryPaths.Resolve(ProvisionerRelativePath.Split('/')));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the store provisioner.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await standardOutput, await standardError);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
