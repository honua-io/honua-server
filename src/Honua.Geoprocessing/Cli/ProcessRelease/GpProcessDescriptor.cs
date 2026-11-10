// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing.Release;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Honua.Geoprocessing.Cli.Release;

internal sealed class GpProcessDescriptor
{
    public const string SchemaId = "honua.gp-process/v1";

    public string? Schema { get; set; }

    public ProcessSection? Process { get; set; }

    public SourceSection? Source { get; set; }

    public string? DependencyLock { get; set; }

    public string? RuntimeImage { get; set; }

    public string? WorkflowVersion { get; set; }

    public List<ConfigurationEntry>? Configuration { get; set; }

    public List<TargetEntry>? Targets { get; set; }

    public CompatibilitySection? Compatibility { get; set; }

    public ArtifactsSection? Artifacts { get; set; }

    public ValidationSection? Validation { get; set; }

    public AuthoringSection? Authoring { get; set; }

    public sealed class AuthoringSection
    {
        public string? Template { get; set; }

        public string? TemplateCommit { get; set; }

        public string? ToolchainVersion { get; set; }

        public string? HarnessRoot { get; set; }

        public string? Source { get; set; }

        public string? Build { get; set; }

        public string? AssemblyDir { get; set; }

        public string? HarnessDll { get; set; }

        public FixturesSection? Fixtures { get; set; }

        public string? Expected { get; set; }
    }

    public sealed class FixturesSection
    {
        public string? Params { get; set; }

        public Dictionary<string, string>? Inputs { get; set; }
    }

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

        Check(Validation?.Receipts is { Count: > 0 }, "validation.receipts must name at least one candidate-bound honua.gp-validation/v1 receipt");
        foreach (var receipt in Validation?.Receipts ?? [])
        {
            Check(!string.IsNullOrWhiteSpace(receipt.Name), "validation.receipts[].name is required");
            Check(!string.IsNullOrWhiteSpace(receipt.Path), $"validation.receipts[{receipt.Name}].path is required");
        }

        return errors;
    }

    public sealed class ProcessSection
    {
        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Runtime { get; set; }

        public string? Entrypoint { get; set; }

        public ContractSection? Contract { get; set; }
    }

    public sealed class ContractSection
    {
        public string? InputSchema { get; set; }

        public string? OutputSchema { get; set; }
    }

    public sealed class SourceSection
    {
        public string? Repository { get; set; }
    }

    public sealed class ConfigurationEntry
    {
        public string? Name { get; set; }

        public string? Reference { get; set; }
    }

    public sealed class TargetEntry
    {
        public string? Kind { get; set; }

        public string? WorkerImageDigest { get; set; }

        public string? ChartVersion { get; set; }

        public string? JobDefinitionName { get; set; }
    }

    public sealed class CompatibilitySection
    {
        public WindowSection? ServerContractVersion { get; set; }
    }

    public sealed class WindowSection
    {
        public int Minimum { get; set; }

        public int Maximum { get; set; }
    }

    public sealed class ArtifactsSection
    {
        public List<string>? Include { get; set; }
    }

    public sealed class ValidationSection
    {
        public List<ReceiptEntry>? Receipts { get; set; }
    }

    public sealed class ReceiptEntry
    {
        public string? Name { get; set; }

        public string? Path { get; set; }
    }
}

internal sealed class GpReleaseException : Exception
{
    public GpReleaseException(IReadOnlyList<string> errors)
        : base(string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
