// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Honua.Geoprocessing.Release;

/// <summary>
/// The immutable identity of one geoprocessing process release
/// (<c>honua.gp-process-release-lock/v1</c>, honua-server#6221): the exact source commit,
/// dependency lock, runtime image, workflow version, configuration references (references,
/// never values), execution targets, artifact digests and validation receipts that a
/// deployment may activate. The wire form is the JSON Schema published by honua-release
/// (<c>schemas/gp-process-release-lock.v1.schema.json</c>); this type is the server-side
/// reader/writer for exactly that document and nothing more.
/// </summary>
/// <remarks>
/// A release is identified by <see cref="Digest"/>: the SHA-256 of the lock's canonical JSON
/// (keys sorted ordinally at every level, no whitespace, UTF-8 without ASCII escaping), which is
/// byte-identical to Python's <c>json.dumps(sort_keys=True, separators=(",", ":"),
/// ensure_ascii=False)</c> used by <c>tools/gp_process_release.py lock-digest</c>. Formatting of
/// the file on disk therefore never changes a release's identity.
/// </remarks>
public sealed record GpProcessReleaseLock
{
    /// <summary>The schema identifier every lock carries.</summary>
    public const string SchemaId = "honua.gp-process-release-lock/v1";

    /// <summary>The process identity and contract.</summary>
    public required GpReleaseProcess Process { get; init; }

    /// <summary>The source commit the release was built from.</summary>
    public required GpReleaseSource Source { get; init; }

    /// <summary>The dependency lock file and its digest.</summary>
    public required GpReleaseFileDigest DependencyLock { get; init; }

    /// <summary>The runtime image the process executes in, pinned by digest.</summary>
    public required GpReleaseRuntimeImage RuntimeImage { get; init; }

    /// <summary>The serving-side workflow version the release targets.</summary>
    public required string WorkflowVersion { get; init; }

    /// <summary>Configuration the worker resolves at execution time; references only.</summary>
    public required IReadOnlyList<GpReleaseConfigurationReference> ConfigurationReferences { get; init; }

    /// <summary>Execution targets the release may be activated on.</summary>
    public required IReadOnlyList<GpReleaseTarget> Targets { get; init; }

    /// <summary>Server execution-contract compatibility window.</summary>
    public required GpReleaseCompatibility Compatibility { get; init; }

    /// <summary>Release artifacts (the built process bundle) by name, digest and size.</summary>
    public required IReadOnlyList<GpReleaseArtifact> Artifacts { get; init; }

    /// <summary>Validation receipts that passed before the lock was written.</summary>
    public required IReadOnlyList<GpReleaseValidationReceipt> ValidationReceipts { get; init; }

    /// <summary>UTC instant the lock was generated, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public required string GeneratedAt { get; init; }

    /// <summary>
    /// The release identity: <c>sha256:&lt;hex&gt;</c> of <see cref="ToCanonicalJson"/>.
    /// </summary>
    public string Digest => Sha256Of(ToCanonicalJsonBytes());

    /// <summary>The first twelve hex characters of the digest, used for lock file names.</summary>
    public string ShortDigest => Digest.Substring("sha256:".Length, 12);

    /// <summary>Serializes the lock in canonical form (see the type remarks).</summary>
    public string ToCanonicalJson() => Encoding.UTF8.GetString(ToCanonicalJsonBytes());

    /// <summary>Serializes the lock in canonical form as UTF-8 bytes.</summary>
    public byte[] ToCanonicalJsonBytes() => Serialize(indented: false);

    /// <summary>
    /// Serializes the lock for a file on disk: the same sorted keys, two-space indented so the
    /// committed file matches the release repository's <c>json.dumps(indent=2, sort_keys=True)</c>
    /// convention and diffs stay readable. Identity is unaffected by formatting.
    /// </summary>
    public string ToIndentedJson() => Encoding.UTF8.GetString(Serialize(indented: true)) + "\n";

    /// <summary>Parses a lock document and validates it against the v1 contract.</summary>
    /// <param name="json">The lock JSON, in any formatting.</param>
    /// <returns>The parsed lock.</returns>
    /// <exception cref="GpProcessReleaseLockException">The document is not a valid v1 lock.</exception>
    public static GpProcessReleaseLock Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new GpProcessReleaseLockException(["lock is not valid JSON: " + ex.Message]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new GpProcessReleaseLockException(["lock must be a JSON object"]);
            }

            var errors = new List<string>();
            var result = new LockReader(errors).Read(root);
            if (errors.Count > 0 || result is null)
            {
                throw new GpProcessReleaseLockException(errors);
            }

            var validation = result.Validate();
            if (validation.Count > 0)
            {
                throw new GpProcessReleaseLockException(validation);
            }

            return result;
        }
    }

    /// <summary>
    /// Validates every field against the v1 schema's patterns and bounds. An empty list means
    /// the lock is valid; otherwise each entry names the offending field.
    /// </summary>
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

        Check(LockPatterns.ProcessName.IsMatch(Process.Name), "process.name must match ^[a-z][a-z0-9-]{1,62}$");
        Check(LockPatterns.SemVer.IsMatch(Process.Version), "process.version must be a semantic version");
        Check(Process.Runtime is "python" or "dotnet", "process.runtime must be python or dotnet");
        Check(Process.Entrypoint.Length > 0, "process.entrypoint must not be empty");
        Check(LockPatterns.Sha256.IsMatch(Process.Contract.InputSchemaSha256), "process.contract.inputSchemaSha256 must be sha256:<64 hex>");
        Check(LockPatterns.Sha256.IsMatch(Process.Contract.OutputSchemaSha256), "process.contract.outputSchemaSha256 must be sha256:<64 hex>");
        Check(LockPatterns.Repository.IsMatch(Source.Repository), "source.repository must be owner/name");
        Check(LockPatterns.GitSha.IsMatch(Source.Sha), "source.sha must be a full 40-hex commit SHA");
        Check(LockPatterns.DescriptorPath.IsMatch(Source.DescriptorPath), "source.descriptorPath must be a .yaml or .yml path without spaces");
        Check(DependencyLock.Path.Length > 0, "dependencyLock.path must not be empty");
        Check(LockPatterns.Sha256.IsMatch(DependencyLock.Sha256), "dependencyLock.sha256 must be sha256:<64 hex>");
        Check(LockPatterns.ImageReference.IsMatch(RuntimeImage.Reference), "runtimeImage.reference must be registry/repository@sha256:<64 hex>");
        Check(LockPatterns.Sha256.IsMatch(RuntimeImage.Digest), "runtimeImage.digest must be sha256:<64 hex>");
        Check(RuntimeImage.Reference.EndsWith("@" + RuntimeImage.Digest, StringComparison.Ordinal), "runtimeImage.reference must end with runtimeImage.digest");
        Check(LockPatterns.WorkflowVersion.IsMatch(WorkflowVersion), "workflowVersion must match ^[A-Za-z0-9._-]{1,64}$");
        foreach (var reference in ConfigurationReferences)
        {
            Check(LockPatterns.ConfigurationName.IsMatch(reference.Name), $"configurationReferences[{reference.Name}].name must match ^[A-Z][A-Z0-9_]{{0,63}}$");
            Check(LockPatterns.ConfigurationReference.IsMatch(reference.Reference), $"configurationReferences[{reference.Name}].reference must be an aws:secretsmanager:, azure:keyvault: or ref: reference, never a value");
        }

        Check(Targets.Count > 0, "targets must name at least one execution target");
        foreach (var target in Targets)
        {
            Check(target.Kind is "kubernetes-job" or "aws-batch", $"targets[{target.Kind}].kind must be kubernetes-job or aws-batch");
            Check(LockPatterns.Sha256.IsMatch(target.WorkerImageDigest), $"targets[{target.Kind}].workerImageDigest must be sha256:<64 hex>");
            Check(target.ChartVersion is null || LockPatterns.SemVer.IsMatch(target.ChartVersion), $"targets[{target.Kind}].chartVersion must be a semantic version");
            Check(target.JobDefinitionName is null || LockPatterns.JobDefinitionName.IsMatch(target.JobDefinitionName), $"targets[{target.Kind}].jobDefinitionName must match ^[A-Za-z0-9_-]{{1,128}}$");
        }

        Check(Compatibility.ServerContractVersion.Minimum >= 1, "compatibility.serverContractVersion.minimum must be at least 1");
        Check(Compatibility.ServerContractVersion.Maximum >= Compatibility.ServerContractVersion.Minimum, "compatibility.serverContractVersion.maximum must not be below minimum");
        Check(Artifacts.Count > 0, "artifacts must name at least one artifact");
        foreach (var artifact in Artifacts)
        {
            Check(LockPatterns.ArtifactName.IsMatch(artifact.Name), $"artifacts[{artifact.Name}].name must match ^[A-Za-z0-9][A-Za-z0-9._-]{{0,127}}$");
            Check(LockPatterns.Sha256.IsMatch(artifact.Sha256), $"artifacts[{artifact.Name}].sha256 must be sha256:<64 hex>");
            Check(artifact.SizeBytes >= 1, $"artifacts[{artifact.Name}].sizeBytes must be at least 1");
        }

        Check(ValidationReceipts.Count > 0, "validation.receipts must hold at least one passed receipt");
        foreach (var receipt in ValidationReceipts)
        {
            Check(receipt.Name.Length > 0, "validation.receipts[].name must not be empty");
            Check(LockPatterns.Sha256.IsMatch(receipt.Sha256), $"validation.receipts[{receipt.Name}].sha256 must be sha256:<64 hex>");
            Check(receipt.Status == "pass", $"validation.receipts[{receipt.Name}].status must be pass");
        }

        Check(LockPatterns.TryParseGeneratedAt(GeneratedAt, out _), "generatedAt must be yyyy-MM-ddTHH:mm:ssZ");
        return errors;
    }

    /// <summary>Formats a UTC instant the way <see cref="GeneratedAt"/> requires.</summary>
    public static string FormatGeneratedAt(DateTimeOffset instant)
        => instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Computes <c>sha256:&lt;hex&gt;</c> over the given bytes.</summary>
    public static string Sha256Of(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private byte[] Serialize(bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            // Keys are written in ordinal order at every level: that IS the canonical form.
            writer.WriteStartObject();
            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            foreach (var artifact in Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("name", artifact.Name);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteNumber("sizeBytes", artifact.SizeBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("compatibility");
            writer.WriteStartObject();
            writer.WritePropertyName("serverContractVersion");
            writer.WriteStartObject();
            writer.WriteNumber("maximum", Compatibility.ServerContractVersion.Maximum);
            writer.WriteNumber("minimum", Compatibility.ServerContractVersion.Minimum);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("configurationReferences");
            writer.WriteStartArray();
            foreach (var reference in ConfigurationReferences)
            {
                writer.WriteStartObject();
                writer.WriteString("name", reference.Name);
                writer.WriteString("reference", reference.Reference);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("dependencyLock");
            writer.WriteStartObject();
            writer.WriteString("path", DependencyLock.Path);
            writer.WriteString("sha256", DependencyLock.Sha256);
            writer.WriteEndObject();
            writer.WriteString("generatedAt", GeneratedAt);
            writer.WritePropertyName("process");
            writer.WriteStartObject();
            writer.WritePropertyName("contract");
            writer.WriteStartObject();
            writer.WriteString("inputSchemaSha256", Process.Contract.InputSchemaSha256);
            writer.WriteString("outputSchemaSha256", Process.Contract.OutputSchemaSha256);
            writer.WriteEndObject();
            writer.WriteString("entrypoint", Process.Entrypoint);
            writer.WriteString("name", Process.Name);
            writer.WriteString("runtime", Process.Runtime);
            writer.WriteString("version", Process.Version);
            writer.WriteEndObject();
            writer.WritePropertyName("runtimeImage");
            writer.WriteStartObject();
            writer.WriteString("digest", RuntimeImage.Digest);
            writer.WriteString("reference", RuntimeImage.Reference);
            writer.WriteEndObject();
            writer.WriteString("schema", SchemaId);
            writer.WritePropertyName("source");
            writer.WriteStartObject();
            writer.WriteString("descriptorPath", Source.DescriptorPath);
            writer.WriteString("repository", Source.Repository);
            writer.WriteString("sha", Source.Sha);
            writer.WriteEndObject();
            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            foreach (var target in Targets)
            {
                writer.WriteStartObject();
                if (target.ChartVersion is not null)
                {
                    writer.WriteString("chartVersion", target.ChartVersion);
                }

                if (target.JobDefinitionName is not null)
                {
                    writer.WriteString("jobDefinitionName", target.JobDefinitionName);
                }

                writer.WriteString("kind", target.Kind);
                writer.WriteString("workerImageDigest", target.WorkerImageDigest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("validation");
            writer.WriteStartObject();
            writer.WritePropertyName("receipts");
            writer.WriteStartArray();
            foreach (var receipt in ValidationReceipts)
            {
                writer.WriteStartObject();
                writer.WriteString("name", receipt.Name);
                writer.WriteString("sha256", receipt.Sha256);
                writer.WriteString("status", receipt.Status);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("workflowVersion", WorkflowVersion);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private sealed class LockReader(List<string> errors)
    {
        private static readonly string[] RootKeys =
        [
            "schema", "process", "source", "dependencyLock", "runtimeImage", "workflowVersion",
            "configurationReferences", "targets", "compatibility", "artifacts", "validation", "generatedAt",
        ];

        public GpProcessReleaseLock? Read(JsonElement root)
        {
            RejectUnknown(root, "", RootKeys);
            var schema = String(root, "", "schema");
            if (schema is not null && schema != SchemaId)
            {
                errors.Add($"schema must be {SchemaId}");
            }

            var process = Object(root, "", "process");
            var source = Object(root, "", "source");
            var dependencyLock = Object(root, "", "dependencyLock");
            var runtimeImage = Object(root, "", "runtimeImage");
            var compatibility = Object(root, "", "compatibility");
            var validation = Object(root, "", "validation");
            var workflowVersion = String(root, "", "workflowVersion");
            var generatedAt = String(root, "", "generatedAt");
            var configuration = ArrayOf(root, "", "configurationReferences");
            var targets = ArrayOf(root, "", "targets");
            var artifacts = ArrayOf(root, "", "artifacts");

            JsonElement? contract = null;
            if (process is { } p)
            {
                RejectUnknown(p, "process", ["name", "version", "runtime", "entrypoint", "contract"]);
                contract = Object(p, "process", "contract");
                if (contract is { } c)
                {
                    RejectUnknown(c, "process.contract", ["inputSchemaSha256", "outputSchemaSha256"]);
                }
            }

            if (source is { } s)
            {
                RejectUnknown(s, "source", ["repository", "sha", "descriptorPath"]);
            }

            if (dependencyLock is { } d)
            {
                RejectUnknown(d, "dependencyLock", ["path", "sha256"]);
            }

            if (runtimeImage is { } r)
            {
                RejectUnknown(r, "runtimeImage", ["reference", "digest"]);
            }

            JsonElement? serverContract = null;
            if (compatibility is { } comp)
            {
                RejectUnknown(comp, "compatibility", ["serverContractVersion"]);
                serverContract = Object(comp, "compatibility", "serverContractVersion");
                if (serverContract is { } sc)
                {
                    RejectUnknown(sc, "compatibility.serverContractVersion", ["minimum", "maximum"]);
                }
            }

            JsonElement? receipts = null;
            if (validation is { } v)
            {
                RejectUnknown(v, "validation", ["receipts"]);
                receipts = ArrayOf(v, "validation", "receipts");
            }

            if (errors.Count > 0 || process is null || source is null || dependencyLock is null
                || runtimeImage is null || contract is null || serverContract is null)
            {
                return null;
            }

            var configurationList = new List<GpReleaseConfigurationReference>();
            foreach (var item in configuration!.Value.EnumerateArray())
            {
                RejectUnknown(item, "configurationReferences[]", ["name", "reference"]);
                configurationList.Add(new GpReleaseConfigurationReference
                {
                    Name = String(item, "configurationReferences[]", "name") ?? string.Empty,
                    Reference = String(item, "configurationReferences[]", "reference") ?? string.Empty,
                });
            }

            var targetList = new List<GpReleaseTarget>();
            foreach (var item in targets!.Value.EnumerateArray())
            {
                RejectUnknown(item, "targets[]", ["kind", "workerImageDigest", "chartVersion", "jobDefinitionName"]);
                targetList.Add(new GpReleaseTarget
                {
                    Kind = String(item, "targets[]", "kind") ?? string.Empty,
                    WorkerImageDigest = String(item, "targets[]", "workerImageDigest") ?? string.Empty,
                    ChartVersion = OptionalString(item, "targets[]", "chartVersion"),
                    JobDefinitionName = OptionalString(item, "targets[]", "jobDefinitionName"),
                });
            }

            var artifactList = new List<GpReleaseArtifact>();
            foreach (var item in artifacts!.Value.EnumerateArray())
            {
                RejectUnknown(item, "artifacts[]", ["name", "sha256", "sizeBytes"]);
                artifactList.Add(new GpReleaseArtifact
                {
                    Name = String(item, "artifacts[]", "name") ?? string.Empty,
                    Sha256 = String(item, "artifacts[]", "sha256") ?? string.Empty,
                    SizeBytes = Integer(item, "artifacts[]", "sizeBytes"),
                });
            }

            var receiptList = new List<GpReleaseValidationReceipt>();
            foreach (var item in receipts!.Value.EnumerateArray())
            {
                RejectUnknown(item, "validation.receipts[]", ["name", "sha256", "status"]);
                receiptList.Add(new GpReleaseValidationReceipt
                {
                    Name = String(item, "validation.receipts[]", "name") ?? string.Empty,
                    Sha256 = String(item, "validation.receipts[]", "sha256") ?? string.Empty,
                    Status = String(item, "validation.receipts[]", "status") ?? string.Empty,
                });
            }

            if (errors.Count > 0)
            {
                return null;
            }

            return new GpProcessReleaseLock
            {
                Process = new GpReleaseProcess
                {
                    Name = String(process!.Value, "process", "name") ?? string.Empty,
                    Version = String(process.Value, "process", "version") ?? string.Empty,
                    Runtime = String(process.Value, "process", "runtime") ?? string.Empty,
                    Entrypoint = String(process.Value, "process", "entrypoint") ?? string.Empty,
                    Contract = new GpReleaseContract
                    {
                        InputSchemaSha256 = String(contract!.Value, "process.contract", "inputSchemaSha256") ?? string.Empty,
                        OutputSchemaSha256 = String(contract.Value, "process.contract", "outputSchemaSha256") ?? string.Empty,
                    },
                },
                Source = new GpReleaseSource
                {
                    Repository = String(source!.Value, "source", "repository") ?? string.Empty,
                    Sha = String(source.Value, "source", "sha") ?? string.Empty,
                    DescriptorPath = String(source.Value, "source", "descriptorPath") ?? string.Empty,
                },
                DependencyLock = new GpReleaseFileDigest
                {
                    Path = String(dependencyLock!.Value, "dependencyLock", "path") ?? string.Empty,
                    Sha256 = String(dependencyLock.Value, "dependencyLock", "sha256") ?? string.Empty,
                },
                RuntimeImage = new GpReleaseRuntimeImage
                {
                    Reference = String(runtimeImage!.Value, "runtimeImage", "reference") ?? string.Empty,
                    Digest = String(runtimeImage.Value, "runtimeImage", "digest") ?? string.Empty,
                },
                WorkflowVersion = workflowVersion ?? string.Empty,
                ConfigurationReferences = configurationList,
                Targets = targetList,
                Compatibility = new GpReleaseCompatibility
                {
                    ServerContractVersion = new GpReleaseContractWindow
                    {
                        Minimum = ContractInteger(serverContract.Value, "minimum"),
                        Maximum = ContractInteger(serverContract.Value, "maximum"),
                    },
                },
                Artifacts = artifactList,
                ValidationReceipts = receiptList,
                GeneratedAt = generatedAt ?? string.Empty,
            };
        }

        private int ContractInteger(JsonElement element, string name)
        {
            var value = Integer(element, "compatibility.serverContractVersion", name);
            if (value is < int.MinValue or > int.MaxValue)
            {
                errors.Add($"compatibility.serverContractVersion.{name} must be an Int32 integer");
                return 0;
            }

            return (int)value;
        }

        private void RejectUnknown(JsonElement element, string path, string[] allowed)
        {
            foreach (var property in element.EnumerateObject().Where(property => Array.IndexOf(allowed, property.Name) < 0))
            {
                errors.Add($"{Join(path, property.Name)} is not part of the v1 lock");
            }
        }

        private string? String(JsonElement element, string path, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                errors.Add($"{Join(path, name)} is required");
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{Join(path, name)} must be a string");
                return null;
            }

            return value.GetString();
        }

        private string? OptionalString(JsonElement element, string path, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{Join(path, name)} must be a string");
                return null;
            }

            return value.GetString();
        }

        private long Integer(JsonElement element, string path, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                errors.Add($"{Join(path, name)} is required");
                return 0;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            {
                errors.Add($"{Join(path, name)} must be an integer");
                return 0;
            }

            return number;
        }

        private JsonElement? Object(JsonElement element, string path, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                errors.Add($"{Join(path, name)} is required");
                return null;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{Join(path, name)} must be an object");
                return null;
            }

            return value;
        }

        private JsonElement? ArrayOf(JsonElement element, string path, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                errors.Add($"{Join(path, name)} is required");
                return null;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{Join(path, name)} must be an array");
                return null;
            }

            return value;
        }

        private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;
    }
}

/// <summary>Process identity inside a release lock.</summary>
public sealed record GpReleaseProcess
{
    /// <summary>Logical process name (<c>^[a-z][a-z0-9-]{1,62}$</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version of the process.</summary>
    public required string Version { get; init; }

    /// <summary><c>python</c> or <c>dotnet</c>.</summary>
    public required string Runtime { get; init; }

    /// <summary>Runtime entrypoint, e.g. <c>buffer_features:execute</c>.</summary>
    public required string Entrypoint { get; init; }

    /// <summary>Digests of the input and output JSON schemas.</summary>
    public required GpReleaseContract Contract { get; init; }
}

/// <summary>Input/output contract digests.</summary>
public sealed record GpReleaseContract
{
    /// <summary>Digest of the input schema file.</summary>
    public required string InputSchemaSha256 { get; init; }

    /// <summary>Digest of the output schema file.</summary>
    public required string OutputSchemaSha256 { get; init; }
}

/// <summary>The source commit of a release.</summary>
public sealed record GpReleaseSource
{
    /// <summary><c>owner/name</c> of the repository holding the process.</summary>
    public required string Repository { get; init; }

    /// <summary>Full 40-hex commit SHA.</summary>
    public required string Sha { get; init; }

    /// <summary>Path of the descriptor inside the repository.</summary>
    public required string DescriptorPath { get; init; }
}

/// <summary>A file path and its digest.</summary>
public sealed record GpReleaseFileDigest
{
    /// <summary>Repository-relative path.</summary>
    public required string Path { get; init; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the file bytes.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>A runtime image pinned by digest.</summary>
public sealed record GpReleaseRuntimeImage
{
    /// <summary><c>registry/repository@sha256:&lt;hex&gt;</c>.</summary>
    public required string Reference { get; init; }

    /// <summary>The digest part of <see cref="Reference"/>.</summary>
    public required string Digest { get; init; }
}

/// <summary>A configuration reference the worker resolves at execution time.</summary>
public sealed record GpReleaseConfigurationReference
{
    /// <summary>Environment-variable style name.</summary>
    public required string Name { get; init; }

    /// <summary>An <c>aws:secretsmanager:</c>, <c>azure:keyvault:</c> or <c>ref:</c> reference, never a value.</summary>
    public required string Reference { get; init; }
}

/// <summary>An execution target a release may be activated on.</summary>
public sealed record GpReleaseTarget
{
    /// <summary><c>kubernetes-job</c> or <c>aws-batch</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Digest of the worker image this target runs.</summary>
    public required string WorkerImageDigest { get; init; }

    /// <summary>Chart version for Kubernetes targets.</summary>
    public string? ChartVersion { get; init; }

    /// <summary>Job definition name for AWS Batch targets.</summary>
    public string? JobDefinitionName { get; init; }
}

/// <summary>Server execution-contract compatibility.</summary>
public sealed record GpReleaseCompatibility
{
    /// <summary>The inclusive window of server contract versions the release supports.</summary>
    public required GpReleaseContractWindow ServerContractVersion { get; init; }
}

/// <summary>An inclusive integer window.</summary>
public sealed record GpReleaseContractWindow
{
    /// <summary>Lowest supported version.</summary>
    public required int Minimum { get; init; }

    /// <summary>Highest supported version.</summary>
    public required int Maximum { get; init; }
}

/// <summary>A release artifact by name, digest and size.</summary>
public sealed record GpReleaseArtifact
{
    /// <summary>File name of the artifact.</summary>
    public required string Name { get; init; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the artifact bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size in bytes.</summary>
    public required long SizeBytes { get; init; }
}

/// <summary>A validation receipt that passed before the release was locked.</summary>
public sealed record GpReleaseValidationReceipt
{
    /// <summary>Receipt name, e.g. <c>golden-tests.junit.xml</c>.</summary>
    public required string Name { get; init; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the receipt bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Always <c>pass</c>; a failed receipt never reaches a lock.</summary>
    public required string Status { get; init; }
}

/// <summary>Raised when a document is not a valid v1 release lock.</summary>
public sealed class GpProcessReleaseLockException : Exception
{
    /// <summary>Creates the exception from the validation findings.</summary>
    public GpProcessReleaseLockException(IReadOnlyList<string> errors)
        : base("Invalid GP process release lock: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    /// <summary>Every finding, one per offending field.</summary>
    public IReadOnlyList<string> Errors { get; }
}

internal static partial class LockPatterns
{
    public static readonly Regex Sha256 = Sha256Regex();
    public static readonly Regex SemVer = SemVerRegex();
    public static readonly Regex ProcessName = ProcessNameRegex();
    public static readonly Regex Repository = RepositoryRegex();
    public static readonly Regex GitSha = GitShaRegex();
    public static readonly Regex DescriptorPath = DescriptorPathRegex();
    public static readonly Regex ImageReference = ImageReferenceRegex();
    public static readonly Regex WorkflowVersion = WorkflowVersionRegex();
    public static readonly Regex ConfigurationName = ConfigurationNameRegex();
    public static readonly Regex ConfigurationReference = ConfigurationReferenceRegex();
    public static readonly Regex JobDefinitionName = JobDefinitionNameRegex();
    public static readonly Regex ArtifactName = ArtifactNameRegex();
    public static readonly Regex GeneratedAt = GeneratedAtRegex();

    public static bool TryParseGeneratedAt(string value, out DateTimeOffset instant)
        => DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerRegex();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,62}$")]
    private static partial Regex ProcessNameRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex GitShaRegex();

    [GeneratedRegex("^[^ ]+\\.ya?ml$")]
    private static partial Regex DescriptorPathRegex();

    [GeneratedRegex("^[a-z0-9.-]+(:[0-9]+)?/[a-z0-9._/-]+@sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageReferenceRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex WorkflowVersionRegex();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex ConfigurationNameRegex();

    [GeneratedRegex("^(aws:secretsmanager:|azure:keyvault:|ref:)[^ ]+$")]
    private static partial Regex ConfigurationReferenceRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex JobDefinitionNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ArtifactNameRegex();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]+)?Z$")]
    private static partial Regex GeneratedAtRegex();
}
