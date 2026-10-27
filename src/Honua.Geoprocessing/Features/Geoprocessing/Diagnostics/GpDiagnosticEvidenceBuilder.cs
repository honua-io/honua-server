// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Honua.Geoprocessing.Diagnostics;

/// <summary>Builds a sanitized <see cref="GpDiagnosticEvidence"/> and re-checks loaded ones.</summary>
internal static partial class GpDiagnosticEvidenceBuilder
{
    private const int MaxInputs = 64;

    private const int MaxEvents = 500;

    private const int MaxAttributes = 32;

    private const int MaxArtifacts = 64;

    private const int MaxDependencies = 64;

    private const int MaxNotes = 32;

    private const int MaxIdLength = 128;

    internal static GpDiagnosticEvidence Build(GpDiagnosticEvidenceDraft draft, GpDiagnosticInputExportRequest? export)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.JobId) || draft.JobId.Length > MaxIdLength)
        {
            throw new GpDiagnosticBundleException("GP evidence job id is missing or too long.");
        }

        var inputs = NormalizeInputs(draft.Inputs);
        var include = export?.IncludeInputData == true;
        GpDiagnosticInputExport? receipt = null;
        if (include)
        {
            receipt = Authorize(export!, inputs);
        }

        var evidence = new GpDiagnosticEvidence
        {
            JobId = draft.JobId.Trim(),
            CorrelationId = GpDiagnosticRedactor.RedactIdentity(draft.CorrelationId),
            TraceId = GpDiagnosticRedactor.RedactIdentity(draft.TraceId),
            Release = new GpDiagnosticReleaseIdentity
            {
                State = GpDiagnosticRedactor.RedactIdentity(draft.ReleaseState) ?? "unavailable",
                SourceRepository = GpDiagnosticRedactor.RedactIdentity(draft.SourceRepository),
                SourceRef = GpDiagnosticRedactor.RedactIdentity(draft.SourceRef),
                WorkflowVersion = GpDiagnosticRedactor.RedactIdentity(draft.WorkflowVersion),
                ProcessRelease = GpDiagnosticRedactor.RedactIdentity(draft.ProcessRelease),
                LockDigest = GpDiagnosticRedactor.RedactDigest(draft.LockDigest),
                Missing = Cap(draft.Missing, MaxNotes).Select(value => GpDiagnosticRedactor.RedactProse(value, 512)).ToArray()
            },
            Runtime = new GpDiagnosticRuntimeIdentity
            {
                ServerVersion = GpDiagnosticRedactor.RedactIdentity(draft.ServerVersion) ?? string.Empty,
                Runtime = GpDiagnosticRedactor.RedactIdentity(draft.Runtime),
                WorkerImage = GpDiagnosticRedactor.RedactIdentity(draft.WorkerImage, 1024),
                Entrypoint = GpDiagnosticRedactor.RedactIdentity(draft.Entrypoint),
                DependencyLockDigest = GpDiagnosticRedactor.RedactDigest(draft.DependencyLockDigest)
            },
            Dependencies = Cap(draft.Dependencies, MaxDependencies).Select(RedactDependency).ToArray(),
            Error = draft.ErrorMessage is null
                ? null
                : new GpDiagnosticError
                {
                    Message = GpDiagnosticRedactor.RedactProse(draft.ErrorMessage),
                    Classification = GpDiagnosticRedactor.RedactIdentity(draft.ErrorClassification),
                    Phase = GpDiagnosticRedactor.RedactIdentity(draft.ErrorPhase),
                    StepOrdinal = draft.StepOrdinal,
                    ErrorCode = GpDiagnosticRedactor.RedactIdentity(draft.ErrorCode),
                    NativeExitCode = draft.NativeExitCode,
                    StderrExcerpt = draft.StderrExcerpt is null ? null : GpDiagnosticRedactor.RedactProse(draft.StderrExcerpt)
                },
            Events = Cap(draft.Events, MaxEvents).Select(RedactEvent).ToArray(),
            EventsTruncated = draft.EventsTruncated || draft.Events.Count > MaxEvents,
            Artifacts = Cap(draft.Artifacts, MaxArtifacts).Select(RedactArtifact).ToArray(),
            ArtifactsState = GpDiagnosticRedactor.RedactIdentity(draft.ArtifactsState),
            Inputs = inputs.Select(Describe).ToArray(),
            InputDataIncluded = receipt is not null,
            InputExport = receipt,
            Notes = Cap(draft.Notes, MaxNotes).Select(value => GpDiagnosticRedactor.RedactProse(value, 512)).ToArray()
        };

        return Accept(evidence);
    }

    /// <summary>
    /// Re-applies redaction to an attachment read from a bundle. Input bytes without an
    /// authorizing identity are refused before the attachment is returned.
    /// </summary>
    internal static GpDiagnosticEvidence Accept(GpDiagnosticEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!string.Equals(evidence.Schema, GpDiagnosticEvidence.SchemaId, StringComparison.Ordinal))
        {
            throw new GpDiagnosticBundleException("GP evidence attachment schema is not supported.");
        }

        if (string.IsNullOrWhiteSpace(evidence.JobId))
        {
            throw new GpDiagnosticBundleException("GP evidence job id is missing.");
        }

        var export = evidence.InputExport;
        var includedContent = export?.Included?.Any(item => !string.IsNullOrEmpty(item.ContentBase64)) == true;
        if ((evidence.InputDataIncluded || includedContent) && string.IsNullOrWhiteSpace(export?.AuthorizedBy))
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input data is present or was requested, but no authorizing identity was recorded.");
        }

        if (evidence.InputDataIncluded && export is null)
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input export was requested but no authorization record was stored.");
        }

        return evidence with
        {
            InputDataIncluded = evidence.InputDataIncluded || includedContent,
            JobId = evidence.JobId.Trim(),
            CorrelationId = GpDiagnosticRedactor.RedactIdentity(evidence.CorrelationId),
            TraceId = GpDiagnosticRedactor.RedactIdentity(evidence.TraceId),
            Release = evidence.Release is null ? null : evidence.Release with
            {
                State = GpDiagnosticRedactor.RedactIdentity(evidence.Release.State) ?? "unavailable",
                SourceRepository = GpDiagnosticRedactor.RedactIdentity(evidence.Release.SourceRepository),
                SourceRef = GpDiagnosticRedactor.RedactIdentity(evidence.Release.SourceRef),
                WorkflowVersion = GpDiagnosticRedactor.RedactIdentity(evidence.Release.WorkflowVersion),
                ProcessRelease = GpDiagnosticRedactor.RedactIdentity(evidence.Release.ProcessRelease),
                LockDigest = GpDiagnosticRedactor.RedactDigest(evidence.Release.LockDigest),
                Missing = OrEmpty(evidence.Release.Missing).Select(value => GpDiagnosticRedactor.RedactProse(value, 512)).ToArray()
            },
            Runtime = evidence.Runtime is null ? null : evidence.Runtime with
            {
                ServerVersion = GpDiagnosticRedactor.RedactIdentity(evidence.Runtime.ServerVersion) ?? string.Empty,
                Runtime = GpDiagnosticRedactor.RedactIdentity(evidence.Runtime.Runtime),
                WorkerImage = GpDiagnosticRedactor.RedactIdentity(evidence.Runtime.WorkerImage, 1024),
                Entrypoint = GpDiagnosticRedactor.RedactIdentity(evidence.Runtime.Entrypoint),
                DependencyLockDigest = GpDiagnosticRedactor.RedactDigest(evidence.Runtime.DependencyLockDigest)
            },
            Dependencies = OrEmpty(evidence.Dependencies).Select(item => item with
            {
                Name = GpDiagnosticRedactor.RedactIdentity(item.Name) ?? GpDiagnosticRedactor.Redacted,
                Version = GpDiagnosticRedactor.RedactIdentity(item.Version),
                Digest = GpDiagnosticRedactor.RedactDigest(item.Digest)
            }).ToArray(),
            Error = evidence.Error is null ? null : evidence.Error with
            {
                Message = GpDiagnosticRedactor.RedactProse(evidence.Error.Message),
                Classification = GpDiagnosticRedactor.RedactIdentity(evidence.Error.Classification),
                Phase = GpDiagnosticRedactor.RedactIdentity(evidence.Error.Phase),
                ErrorCode = GpDiagnosticRedactor.RedactIdentity(evidence.Error.ErrorCode),
                StderrExcerpt = evidence.Error.StderrExcerpt is null
                    ? null
                    : GpDiagnosticRedactor.RedactProse(evidence.Error.StderrExcerpt)
            },
            Events = OrEmpty(evidence.Events).Select(RedactLoadedEvent).ToArray(),
            Artifacts = OrEmpty(evidence.Artifacts).Select(item => item with
            {
                Id = GpDiagnosticRedactor.RedactIdentity(item.Id) ?? GpDiagnosticRedactor.Redacted,
                Kind = GpDiagnosticRedactor.RedactIdentity(item.Kind),
                Availability = GpDiagnosticRedactor.RedactIdentity(item.Availability),
                Label = item.Label is null ? null : GpDiagnosticRedactor.RedactProse(item.Label, 256),
                Sha256 = GpDiagnosticRedactor.RedactDigest(item.Sha256)
            }).ToArray(),
            ArtifactsState = GpDiagnosticRedactor.RedactIdentity(evidence.ArtifactsState),
            Inputs = OrEmpty(evidence.Inputs).Select(item => item with
            {
                Id = GpDiagnosticRedactor.RedactIdentity(item.Id) ?? GpDiagnosticRedactor.Redacted,
                Role = GpDiagnosticRedactor.RedactIdentity(item.Role) ?? "input",
                MediaType = GpDiagnosticRedactor.RedactIdentity(item.MediaType),
                Reference = item.Reference is null ? null : GpDiagnosticRedactor.RedactProse(item.Reference, 2048),
                Sha256 = GpDiagnosticRedactor.RedactDigest(item.Sha256)
            }).ToArray(),
            InputExport = export is null ? null : export with
            {
                AuthorizedBy = RequireAuthorizer(export.AuthorizedBy),
                Included = OrEmpty(export.Included).Select(RedactLoadedInclusion).ToArray()
            },
            Notes = OrEmpty(evidence.Notes).Select(value => GpDiagnosticRedactor.RedactProse(value, 512)).ToArray()
        };
    }

    private static T[] OrEmpty<T>(T[]? values)
        => values ?? [];

    private static GpDiagnosticInputExport Authorize(
        GpDiagnosticInputExportRequest export,
        IReadOnlyList<GpDiagnosticInputDraft> inputs)
    {
        var authorizedBy = export.AuthorizedBy?.Trim();
        if (string.IsNullOrWhiteSpace(authorizedBy))
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input export was requested but no authorizing identity was recorded.");
        }

        if (GpDiagnosticRedactor.RedactIdentity(authorizedBy) != authorizedBy)
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input export was requested but the authorizing identity contained a credential.");
        }

        if (export.AuthorizedAt is null)
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input export was requested but no authorization time was recorded.");
        }

        if (export.InputIds is null || export.InputIds.Count == 0)
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input export was requested but no input was authorized.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var included = new List<GpDiagnosticIncludedInput>(export.InputIds.Count);
        foreach (var rawId in export.InputIds)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                throw new GpDiagnosticExportUnauthorizedException(
                    "Input export was requested but an authorized input id was blank.");
            }

            var id = rawId.Trim();
            if (!seen.Add(id))
            {
                throw new GpDiagnosticBundleException("An authorized input id was repeated.");
            }

            var input = inputs.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (input is null)
            {
                throw new GpDiagnosticBundleException("An authorized input is not part of this evidence.");
            }

            included.Add(Include(input));
        }

        return new GpDiagnosticInputExport
        {
            AuthorizedBy = authorizedBy,
            AuthorizedAt = export.AuthorizedAt.Value.ToUniversalTime(),
            Included = included.ToArray()
        };
    }

    private static GpDiagnosticIncludedInput Include(GpDiagnosticInputDraft input)
    {
        if (input.Content is null)
        {
            return new GpDiagnosticIncludedInput
            {
                Id = input.Id,
                ContentIncluded = false,
                UnavailableReason = "input bytes were not supplied",
                Sha256 = GpDiagnosticRedactor.RedactDigest(input.Sha256)
            };
        }

        if (input.Content.Length > GpDiagnosticEvidence.MaxInputBytes)
        {
            throw new GpDiagnosticBundleException("Authorized input exceeds the export byte ceiling.");
        }

        var (bytes, applied) = GpDiagnosticRedactor.RedactContent(input.Content);
        var originalHash = Sha256(input.Content);
        RejectConflictingHash(input.Sha256, originalHash);
        return new GpDiagnosticIncludedInput
        {
            Id = input.Id,
            ContentIncluded = true,
            ByteSize = input.Content.Length,
            Sha256 = originalHash,
            ContentSha256 = Sha256(bytes),
            RedactionApplied = applied,
            ContentBase64 = Convert.ToBase64String(bytes)
        };
    }

    private static List<GpDiagnosticInputDraft> NormalizeInputs(IReadOnlyList<GpDiagnosticInputDraft> inputs)
    {
        if (inputs.Count > MaxInputs)
        {
            throw new GpDiagnosticBundleException("GP evidence has too many inputs.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<GpDiagnosticInputDraft>(inputs.Count);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Id) || input.Id.Trim().Length > MaxIdLength)
            {
                throw new GpDiagnosticBundleException("GP evidence input id is missing or too long.");
            }

            var id = input.Id.Trim();
            if (!seen.Add(id))
            {
                throw new GpDiagnosticBundleException("GP evidence input ids must be unique.");
            }

            if (input.Content is { Length: > GpDiagnosticEvidence.MaxInputBytes })
            {
                throw new GpDiagnosticBundleException("GP evidence input exceeds the hash byte ceiling.");
            }

            normalized.Add(input with { Id = id });
        }

        return normalized;
    }

    private static GpDiagnosticInput Describe(GpDiagnosticInputDraft input)
    {
        string? hash = GpDiagnosticRedactor.RedactDigest(input.Sha256);
        long? size = null;
        if (input.Content is not null)
        {
            var original = Sha256(input.Content);
            RejectConflictingHash(input.Sha256, original);
            hash = original;
            size = input.Content.Length;
        }

        return new GpDiagnosticInput
        {
            Id = input.Id,
            Role = GpDiagnosticRedactor.RedactIdentity(input.Role) ?? "input",
            MediaType = GpDiagnosticRedactor.RedactIdentity(input.MediaType),
            Reference = input.Reference is null ? null : GpDiagnosticRedactor.RedactProse(input.Reference, 2048),
            ByteSize = size,
            Sha256 = hash
        };
    }

    private static void RejectConflictingHash(string? declared, string actual)
    {
        var normalized = GpDiagnosticRedactor.RedactDigest(declared);
        if (normalized is not null && !normalized.EndsWith(actual, StringComparison.Ordinal))
        {
            throw new GpDiagnosticBundleException("GP evidence input hash does not match the supplied bytes.");
        }
    }

    private static GpDiagnosticDependency RedactDependency(GpDiagnosticDependencyDraft dependency)
        => new()
        {
            Name = GpDiagnosticRedactor.RedactIdentity(dependency.Name) ?? GpDiagnosticRedactor.Redacted,
            Version = GpDiagnosticRedactor.RedactIdentity(dependency.Version),
            Digest = GpDiagnosticRedactor.RedactDigest(dependency.Digest)
        };

    private static GpDiagnosticEvent RedactEvent(GpDiagnosticEventDraft draft)
        => new()
        {
            Timestamp = draft.Timestamp,
            Level = GpDiagnosticRedactor.RedactIdentity(draft.Level) ?? "info",
            Phase = GpDiagnosticRedactor.RedactIdentity(draft.Phase),
            Message = GpDiagnosticRedactor.RedactProse(draft.Message),
            Attributes = Cap(draft.Attributes, MaxAttributes).Select(RedactAttribute).ToArray()
        };

    private static GpDiagnosticEvent RedactLoadedEvent(GpDiagnosticEvent draft)
        => draft with
        {
            Level = GpDiagnosticRedactor.RedactIdentity(draft.Level) ?? "info",
            Phase = GpDiagnosticRedactor.RedactIdentity(draft.Phase),
            Message = GpDiagnosticRedactor.RedactProse(draft.Message),
            Attributes = OrEmpty(draft.Attributes).Take(MaxAttributes).Select(item => item with
            {
                Name = GpDiagnosticRedactor.RedactIdentity(item.Name) ?? GpDiagnosticRedactor.Redacted,
                Value = RedactAttributeValue(item.Name, item.Value)
            }).ToArray()
        };

    private static GpDiagnosticAttribute RedactAttribute(GpDiagnosticAttributeDraft attribute)
        => new()
        {
            Name = GpDiagnosticRedactor.RedactIdentity(attribute.Name) ?? GpDiagnosticRedactor.Redacted,
            Value = RedactAttributeValue(attribute.Name, attribute.Value)
        };

    private static string RedactAttributeValue(string name, string value)
    {
        if (SensitiveName().IsMatch(name))
        {
            return value.Contains("://", StringComparison.Ordinal) || value.Contains('=', StringComparison.Ordinal)
                ? GpDiagnosticRedactor.RedactProse(value, 256)
                : GpDiagnosticRedactor.Redacted;
        }

        return GpDiagnosticRedactor.RedactProse(value, 1024);
    }

    private static GpDiagnosticArtifact RedactArtifact(GpDiagnosticArtifactDraft artifact)
        => new()
        {
            Id = GpDiagnosticRedactor.RedactIdentity(artifact.Id) ?? GpDiagnosticRedactor.Redacted,
            Kind = GpDiagnosticRedactor.RedactIdentity(artifact.Kind),
            Availability = GpDiagnosticRedactor.RedactIdentity(artifact.Availability),
            Label = artifact.Label is null ? null : GpDiagnosticRedactor.RedactProse(artifact.Label, 256),
            ByteSize = artifact.ByteSize,
            Sha256 = GpDiagnosticRedactor.RedactDigest(artifact.Sha256)
        };

    private static GpDiagnosticIncludedInput RedactLoadedInclusion(GpDiagnosticIncludedInput item)
    {
        if (string.IsNullOrEmpty(item.ContentBase64))
        {
            return item with
            {
                Id = GpDiagnosticRedactor.RedactIdentity(item.Id) ?? GpDiagnosticRedactor.Redacted,
                UnavailableReason = item.UnavailableReason is null
                    ? null
                    : GpDiagnosticRedactor.RedactProse(item.UnavailableReason, 256),
                Sha256 = GpDiagnosticRedactor.RedactDigest(item.Sha256)
            };
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(item.ContentBase64);
        }
        catch (FormatException)
        {
            throw new GpDiagnosticBundleException("Included input content is not valid base64.");
        }

        if (decoded.Length > GpDiagnosticEvidence.MaxInputBytes)
        {
            throw new GpDiagnosticBundleException("Included input exceeds the export byte ceiling.");
        }

        var (bytes, applied) = GpDiagnosticRedactor.RedactContent(decoded);
        return item with
        {
            Id = GpDiagnosticRedactor.RedactIdentity(item.Id) ?? GpDiagnosticRedactor.Redacted,
            ContentIncluded = true,
            Sha256 = GpDiagnosticRedactor.RedactDigest(item.Sha256),
            ContentSha256 = Sha256(bytes),
            RedactionApplied = item.RedactionApplied || applied,
            ContentBase64 = Convert.ToBase64String(bytes)
        };
    }

    private static string RequireAuthorizer(string authorizedBy)
    {
        var trimmed = authorizedBy.Trim();
        if (trimmed.Length == 0 || GpDiagnosticRedactor.RedactIdentity(trimmed) != trimmed)
        {
            throw new GpDiagnosticExportUnauthorizedException(
                "Input data is present or was requested, but no authorizing identity was recorded.");
        }

        return trimmed;
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static IEnumerable<T> Cap<T>(IReadOnlyList<T> values, int max)
        => values.Take(max);

    [GeneratedRegex(
        "password|passwd|secret|token|authorization|credential|connection|api[-_]?key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex SensitiveName();
}
