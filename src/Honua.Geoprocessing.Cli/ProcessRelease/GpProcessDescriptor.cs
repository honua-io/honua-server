// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing.Release;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Honua.Geoprocessing.Cli.Release;

/// <summary>
/// The <c>honua-gp.yaml</c> project descriptor (<c>honua.gp-process/v1</c>): what a developer
/// commits next to a custom process so <c>honua-gp-devkit release</c> can turn one source commit
/// into an immutable <see cref="GpProcessReleaseLock"/>. Everything here is declared by the
/// author; the devkit resolves digests, the source SHA and the artifact from it and never
/// invents a value that is not in the descriptor or on disk.
/// </summary>
internal sealed class GpProcessDescriptor
{
    /// <summary>The schema identifier every descriptor carries.</summary>
    public const string SchemaId = "honua.gp-process/v1";

    /// <summary>Must be <see cref="SchemaId"/>.</summary>
    public string? Schema { get; set; }

    /// <summary>Process identity and contract files.</summary>
    public ProcessSection? Process { get; set; }

    /// <summary>Source repository identity; the commit SHA always comes from Git or the CLI.</summary>
    public SourceSection? Source { get; set; }

    /// <summary>Descriptor-relative path of the dependency lock (<c>requirements.lock</c>, <c>packages.lock.json</c>, ...).</summary>
    public string? DependencyLock { get; set; }

    /// <summary>Runtime image reference pinned by digest; <c>--runtime-image</c> may override it.</summary>
    public string? RuntimeImage { get; set; }

    /// <summary>The serving-side workflow version the process targets.</summary>
    public string? WorkflowVersion { get; set; }

    /// <summary>Configuration references the worker resolves at execution time.</summary>
    public List<ConfigurationEntry>? Configuration { get; set; }

    /// <summary>Execution targets; <c>workerImageDigest</c> defaults to the runtime image digest.</summary>
    public List<TargetEntry>? Targets { get; set; }

    /// <summary>Server execution-contract window.</summary>
    public CompatibilitySection? Compatibility { get; set; }

    /// <summary>What goes into the release artifact.</summary>
    public ArtifactsSection? Artifacts { get; set; }

    /// <summary>Validation receipts that must exist (and have passed) before a lock is written.</summary>
    public ValidationSection? Validation { get; set; }

    /// <summary>Optional local fixture and toolchain settings shared by init and test.</summary>
    public AuthoringSection? Authoring { get; set; }

    /// <summary>Local development settings; these do not assert remote validation.</summary>
    public sealed class AuthoringSection
    {
        /// <summary>Local Template setting.</summary>
        public string? Template { get; set; }

        /// <summary>Local TemplateCommit setting.</summary>
        public string? TemplateCommit { get; set; }

        /// <summary>Local ToolchainVersion setting.</summary>
        public string? ToolchainVersion { get; set; }

        /// <summary>Local HarnessRoot setting.</summary>
        public string? HarnessRoot { get; set; }

        /// <summary>Local Source setting.</summary>
        public string? Source { get; set; }

        /// <summary>Local Build setting.</summary>
        public string? Build { get; set; }

        /// <summary>Local AssemblyDir setting.</summary>
        public string? AssemblyDir { get; set; }

        /// <summary>Local HarnessDll setting.</summary>
        public string? HarnessDll { get; set; }

        /// <summary>Local Fixtures setting.</summary>
        public FixturesSection? Fixtures { get; set; }

        /// <summary>Local Expected setting.</summary>
        public string? Expected { get; set; }
    }

    /// <summary>Input fixtures used by the local harness.</summary>
    public sealed class FixturesSection
    {
        /// <summary>Local Params setting.</summary>
        public string? Params { get; set; }

        /// <summary>Local Inputs setting.</summary>
        public Dictionary<string, string>? Inputs { get; set; }
    }

    /// <summary>Parses a descriptor document; unknown keys are an error.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The parsed descriptor; call <see cref="Validate"/> before use.</returns>
    /// <exception cref="GpReleaseException">The document is not valid YAML for this schema.</exception>
    public static GpProcessDescriptor Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        try
        {
            var descriptor = deserializer.Deserialize<GpProcessDescriptor>(yaml);
            if (descriptor is null)
            {
                throw new GpReleaseException(["the descriptor is empty"]);
            }

            return descriptor;
        }
        catch (YamlException ex)
        {
            throw new GpReleaseException([$"the descriptor is not valid honua.gp-process/v1 YAML: {ex.Message}"]);
        }
    }

    /// <summary>Checks every declared value; file existence is checked by the builder.</summary>
    /// <returns>One finding per problem; empty when the descriptor is well formed.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                errors.Add(message);
            }
        }

        Check(Schema == SchemaId, $"schema must be {SchemaId}");
        if (Process is null)
        {
            errors.Add("process is required");
        }
        else
        {
            Check(Process.Name is not null && LockPatterns.ProcessName.IsMatch(Process.Name), "process.name must match ^[a-z][a-z0-9-]{1,62}$");
            Check(Process.Version is not null && LockPatterns.SemVer.IsMatch(Process.Version), "process.version must be a semantic version");
            Check(Process.Runtime is "python" or "dotnet", "process.runtime must be python or dotnet");
            Check(!string.IsNullOrWhiteSpace(Process.Entrypoint), "process.entrypoint is required");
            Check(Process.Contract is { InputSchema.Length: > 0, OutputSchema.Length: > 0 }, "process.contract.inputSchema and process.contract.outputSchema are required paths");
        }

        Check(Source?.Repository is null || LockPatterns.Repository.IsMatch(Source.Repository), "source.repository must be owner/name");
        Check(!string.IsNullOrWhiteSpace(DependencyLock), "dependencyLock is required (the path of the committed dependency lock)");
        Check(RuntimeImage is null || LockPatterns.ImageReference.IsMatch(RuntimeImage), "runtimeImage must be registry/repository@sha256:<64 hex>");
        Check(WorkflowVersion is not null && LockPatterns.WorkflowVersion.IsMatch(WorkflowVersion), "workflowVersion must match ^[A-Za-z0-9._-]{1,64}$");
        foreach (var entry in Configuration ?? [])
        {
            Check(entry.Name is not null && LockPatterns.ConfigurationName.IsMatch(entry.Name), $"configuration[{entry.Name}].name must match ^[A-Z][A-Z0-9_]{{0,63}}$");
            Check(entry.Reference is not null && LockPatterns.ConfigurationReference.IsMatch(entry.Reference), $"configuration[{entry.Name}].reference must be an aws:secretsmanager:, azure:keyvault: or ref: reference, never a value");
        }

        Check(Targets is { Count: > 0 }, "targets must name at least one of kubernetes-job or aws-batch");
        foreach (var target in Targets ?? [])
        {
            Check(target.Kind is "kubernetes-job" or "aws-batch", $"targets[{target.Kind}].kind must be kubernetes-job or aws-batch");
            Check(target.WorkerImageDigest is null || LockPatterns.Sha256.IsMatch(target.WorkerImageDigest), $"targets[{target.Kind}].workerImageDigest must be sha256:<64 hex>");
            Check(target.ChartVersion is null || LockPatterns.SemVer.IsMatch(target.ChartVersion), $"targets[{target.Kind}].chartVersion must be a semantic version");
            Check(target.JobDefinitionName is null || LockPatterns.JobDefinitionName.IsMatch(target.JobDefinitionName), $"targets[{target.Kind}].jobDefinitionName must match ^[A-Za-z0-9_-]{{1,128}}$");
        }

        var window = Compatibility?.ServerContractVersion;
        Check(window is not null, "compatibility.serverContractVersion.minimum and .maximum are required");
        if (window is not null)
        {
            Check(window.Minimum >= 1, "compatibility.serverContractVersion.minimum must be at least 1");
            Check(window.Maximum >= window.Minimum, "compatibility.serverContractVersion.maximum must not be below minimum");
        }

        foreach (var include in Artifacts?.Include ?? [])
        {
            Check(!string.IsNullOrWhiteSpace(include) && !Path.IsPathRooted(include) && !include.Split('/', '\\').Contains(".."), $"artifacts.include entry '{include}' must be a relative path inside the project");
        }

        Check(Validation?.Receipts is { Count: > 0 }, "validation.receipts must name at least one receipt file that the release proves passed");
        foreach (var receipt in Validation?.Receipts ?? [])
        {
            Check(!string.IsNullOrWhiteSpace(receipt.Name), "validation.receipts[].name is required");
            Check(!string.IsNullOrWhiteSpace(receipt.Path), $"validation.receipts[{receipt.Name}].path is required");
        }

        return errors;
    }

    /// <summary>The <c>process</c> section.</summary>
    public sealed class ProcessSection
    {
        /// <summary>Logical process name.</summary>
        public string? Name { get; set; }

        /// <summary>Semantic version.</summary>
        public string? Version { get; set; }

        /// <summary><c>python</c> or <c>dotnet</c>.</summary>
        public string? Runtime { get; set; }

        /// <summary>Runtime entrypoint.</summary>
        public string? Entrypoint { get; set; }

        /// <summary>Contract schema paths.</summary>
        public ContractSection? Contract { get; set; }
    }

    /// <summary>Descriptor-relative schema file paths.</summary>
    public sealed class ContractSection
    {
        /// <summary>Input JSON schema path.</summary>
        public string? InputSchema { get; set; }

        /// <summary>Output JSON schema path.</summary>
        public string? OutputSchema { get; set; }
    }

    /// <summary>The <c>source</c> section.</summary>
    public sealed class SourceSection
    {
        /// <summary><c>owner/name</c>; <c>--source-repository</c> may override it.</summary>
        public string? Repository { get; set; }
    }

    /// <summary>One configuration reference.</summary>
    public sealed class ConfigurationEntry
    {
        /// <summary>Environment-variable style name.</summary>
        public string? Name { get; set; }

        /// <summary>A reference, never a value.</summary>
        public string? Reference { get; set; }
    }

    /// <summary>One execution target.</summary>
    public sealed class TargetEntry
    {
        /// <summary><c>kubernetes-job</c> or <c>aws-batch</c>.</summary>
        public string? Kind { get; set; }

        /// <summary>Worker image digest; defaults to the runtime image digest.</summary>
        public string? WorkerImageDigest { get; set; }

        /// <summary>Chart version for Kubernetes targets.</summary>
        public string? ChartVersion { get; set; }

        /// <summary>Job definition name for AWS Batch targets.</summary>
        public string? JobDefinitionName { get; set; }
    }

    /// <summary>The <c>compatibility</c> section.</summary>
    public sealed class CompatibilitySection
    {
        /// <summary>Inclusive server contract window.</summary>
        public WindowSection? ServerContractVersion { get; set; }
    }

    /// <summary>An inclusive integer window.</summary>
    public sealed class WindowSection
    {
        /// <summary>Lowest supported version.</summary>
        public int Minimum { get; set; }

        /// <summary>Highest supported version.</summary>
        public int Maximum { get; set; }
    }

    /// <summary>The <c>artifacts</c> section.</summary>
    public sealed class ArtifactsSection
    {
        /// <summary>
        /// Descriptor-relative files or directories packed into the release artifact. Empty
        /// means the descriptor's whole directory minus build output and tooling folders.
        /// </summary>
        public List<string>? Include { get; set; }
    }

    /// <summary>The <c>validation</c> section.</summary>
    public sealed class ValidationSection
    {
        /// <summary>Receipt files produced by the project's own validation.</summary>
        public List<ReceiptEntry>? Receipts { get; set; }
    }

    /// <summary>One receipt file.</summary>
    public sealed class ReceiptEntry
    {
        /// <summary>Receipt name recorded in the lock.</summary>
        public string? Name { get; set; }

        /// <summary>Descriptor-relative path of the receipt file.</summary>
        public string? Path { get; set; }
    }
}

/// <summary>A release could not be built from the descriptor and the checkout.</summary>
internal sealed class GpReleaseException : Exception
{
    /// <summary>Creates the exception from one or more findings.</summary>
    public GpReleaseException(IReadOnlyList<string> errors)
        : base(string.Join("; ", errors))
    {
        Errors = errors;
    }

    /// <summary>Every finding.</summary>
    public IReadOnlyList<string> Errors { get; }
}
