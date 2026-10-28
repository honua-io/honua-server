// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Diagnostics;
using Honua.Geoprocessing.LocalRunner;
using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// <c>honua-gp-devkit reproduce &lt;bundle&gt;</c> (honua-server#6220). Parses a diagnostic
/// bundle, pins the recorded source SHA and release lock when they are present, and runs
/// the exported inputs through <see cref="GeoprocessingLocalRunner"/>. Production writes,
/// credentials, connection strings and output paths are never reused. A missing or changed
/// input or artifact refuses the run. Exit 0 means the local outcome matched the recorded
/// one, including a recorded failure.
/// </summary>
internal static partial class GpReproduceCommand
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Catalog used to bind exported bytes the way each executor reads them.</summary>
    private static readonly BuiltInProcessCatalog ProcessCatalog = new(logger: null);

    private static readonly HashSet<string> AbsentArtifactAvailability = new(StringComparer.OrdinalIgnoreCase)
    {
        "expired", "unavailable", "missing", "providererror",
    };

    private static readonly HashSet<string> IncompleteArtifactState = new(StringComparer.OrdinalIgnoreCase)
    {
        "partial", "unavailable",
    };

    /// <summary>Runs the verb.</summary>
    /// <param name="args">Everything after <c>reproduce</c>.</param>
    /// <param name="output">Standard output sink.</param>
    /// <param name="error">Error output sink.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>0 when the recorded outcome was reproduced, 1 when it was not, 2 on usage error.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var request = Parse(args);
        if (!File.Exists(request.BundlePath))
        {
            error.WriteLine($"error   : bundle not found: {request.BundlePath}");
            return 1;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(request.BundlePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("error   : could not read the bundle: " + ex.Message);
            return 1;
        }

        if (ContainsCredential(Encoding.UTF8.GetBytes(json)))
        {
            RefuseCredentials(error);
            return 1;
        }

        GpDiagnosticBundleDocument document;
        try
        {
            document = GpDiagnosticBundleDocument.Parse(json);
        }
        catch (GpDiagnosticBundleException ex)
        {
            error.WriteLine("error   : " + ex.Message);
            return 1;
        }

        var evidence = document.Evidence;
        if (evidence is null)
        {
            error.WriteLine(
                "bundle loaded (schemaVersion " + document.SchemaVersion
                + ") but reproduction is incomplete: gpEvidence is absent.");
            return 1;
        }

        if (evidence.InputExport?.Included.Any(static item => item.RedactionApplied) == true
            || ExportedInputsContainCredential(evidence))
        {
            RefuseCredentials(error);
            return 1;
        }

        if (!TryPin(evidence, request, error))
        {
            return 1;
        }

        if (!TryCollectInputs(evidence, error, out var payloads))
        {
            return 1;
        }

        if (!TryResolveProcess(evidence, request.ProcessId, out var processId, out var processError))
        {
            error.WriteLine("error   : " + processError);
            return 1;
        }

        var parameters = new Dictionary<string, string>(payloads.Count, StringComparer.Ordinal);
        foreach (var input in evidence.Inputs)
        {
            parameters[input.Id] = ToParameter(processId, input, payloads[input.Id]);
            if (ContainsCredential(Encoding.UTF8.GetBytes(parameters[input.Id])))
            {
                RefuseCredentials(error);
                return 1;
            }
        }

        var root = Directory.CreateTempSubdirectory("honua-gp-reproduce-");
        LocalRunResult result;
        try
        {
            var scratch = Directory.CreateDirectory(Path.Join(root.FullName, "scratch"));
            Directory.CreateDirectory(Path.Join(root.FullName, "output"));
            foreach (var input in evidence.Inputs)
            {
                var path = Path.Join(scratch.FullName, SafeFileName(input.Id));
                await File.WriteAllBytesAsync(path, payloads[input.Id], cancellationToken).ConfigureAwait(false);
            }

            result = await GpCli.ExecuteLocalAsync(processId, parameters, scratch.FullName, cancellationToken)
                .ConfigureAwait(false);
            await WriteOutputsAsync(root.FullName, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(root.FullName);
        }

        if (result.ErrorMessage is not null
            && result.ErrorMessage.Contains("is not registered with the local runner", StringComparison.Ordinal))
        {
            error.WriteLine("error   : " + result.ErrorMessage);
            return 1;
        }

        return Report(evidence, result, output, error);
    }

    private static ReproduceArgs Parse(string[] args)
    {
        string? bundle = null;
        string? lockPath = null;
        string? checkout = null;
        string? processId = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lock":
                    lockPath = Next(args, ref i, "--lock");
                    break;
                case "--checkout":
                    checkout = Next(args, ref i, "--checkout");
                    break;
                case "--process":
                    processId = Next(args, ref i, "--process");
                    break;
                default:
                    if (args[i].StartsWith('-') || bundle is not null)
                    {
                        throw new GpCliUsageException($"Unknown option '{args[i]}' for reproduce.");
                    }

                    bundle = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(bundle))
        {
            throw new GpCliUsageException(
                "reproduce requires <bundle.json> [--lock <lock.json>] [--checkout <dir>] [--process <id>].");
        }

        return new ReproduceArgs(bundle, lockPath, checkout, processId);
    }

    private static bool TryPin(GpDiagnosticEvidence evidence, ReproduceArgs request, TextWriter error)
    {
        var problems = new List<string>();
        var sourceSha = CommitSha(evidence.Release?.SourceRef);
        if (sourceSha is not null)
        {
            // Compare only. Never check out another commit and never move HEAD.
            var checkout = request.Checkout ?? FindCheckout(Directory.GetCurrentDirectory());
            var head = checkout is null ? null : GitHead(checkout);
            if (head is null)
            {
                problems.Add(
                    "source SHA mismatched: evidence has " + sourceSha
                    + ", checkout HEAD is missing (" + (checkout ?? "no git checkout") + ").");
            }
            else if (!string.Equals(head, sourceSha, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("source SHA mismatched: evidence has " + sourceSha + ", checkout HEAD is " + head + ".");
            }
        }

        var lockDigest = NormalizeDigest(evidence.Release?.LockDigest);
        if (lockDigest is not null)
        {
            if (string.IsNullOrWhiteSpace(request.LockPath))
            {
                problems.Add(
                    "release lock digest mismatched: evidence has sha256:" + lockDigest
                    + ", lock file is missing.");
            }
            else if (!File.Exists(request.LockPath))
            {
                problems.Add(
                    "release lock digest mismatched: evidence has sha256:" + lockDigest
                    + ", lock file is missing (" + request.LockPath + ").");
            }
            else
            {
                try
                {
                    var actual = NormalizeDigest(GpProcessReleaseLock.Parse(File.ReadAllText(request.LockPath)).Digest);
                    if (!string.Equals(actual, lockDigest, StringComparison.Ordinal))
                    {
                        problems.Add(
                            "release lock digest mismatched: evidence has sha256:" + lockDigest
                            + ", lock file has sha256:" + (actual ?? "missing") + ".");
                    }
                }
                catch (GpProcessReleaseLockException ex)
                {
                    problems.Add("release lock digest mismatched: the lock file could not be read (" + ex.Message + ").");
                }
            }
        }

        if (problems.Count == 0)
        {
            return true;
        }

        foreach (var problem in problems)
        {
            error.WriteLine("error   : " + problem);
        }

        return false;
    }

    private static bool TryCollectInputs(
        GpDiagnosticEvidence evidence,
        TextWriter error,
        out Dictionary<string, byte[]> payloads)
    {
        payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var problems = new List<string>();
        var included = evidence.InputExport?.Included ?? [];
        foreach (var input in evidence.Inputs)
        {
            var item = included.FirstOrDefault(candidate => string.Equals(candidate.Id, input.Id, StringComparison.Ordinal));
            var expected = NormalizeDigest(input.Sha256) ?? (item is null ? null : NormalizeDigest(item.Sha256));
            // "" is valid base64 for a zero-byte input. Only a missing property is a gap.
            if (item is null || !item.ContentIncluded || item.ContentBase64 is null)
            {
                problems.Add(Gap("input", input.Id, expected, "missing"));
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(item.ContentBase64);
            }
            catch (FormatException)
            {
                problems.Add(Gap("input", input.Id, expected, "missing"));
                continue;
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (expected is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
            {
                problems.Add(Gap("input", input.Id, expected, actual));
                continue;
            }

            var contentHash = NormalizeDigest(item.ContentSha256);
            if (contentHash is not null && !string.Equals(contentHash, actual, StringComparison.Ordinal))
            {
                problems.Add(Gap("input", input.Id, contentHash, actual));
                continue;
            }

            if (expected is null && contentHash is null)
            {
                problems.Add(Gap("input", input.Id, null, actual));
                continue;
            }

            payloads[input.Id] = bytes;
        }

        foreach (var artifact in evidence.Artifacts)
        {
            var expected = NormalizeDigest(artifact.Sha256);
            var absent = artifact.Availability is not null && AbsentArtifactAvailability.Contains(artifact.Availability);
            if (expected is not null || absent)
            {
                problems.Add(Gap("artifact", artifact.Id, expected, "missing"));
            }
        }

        if (evidence.ArtifactsState is not null && IncompleteArtifactState.Contains(evidence.ArtifactsState))
        {
            problems.Add(Gap("artifact", "state " + evidence.ArtifactsState, null, "missing"));
        }

        if (problems.Count == 0)
        {
            return true;
        }

        error.WriteLine("error   : reproduction is incomplete:");
        foreach (var problem in problems)
        {
            error.WriteLine("          " + problem);
        }

        return false;
    }

    private static int Report(
        GpDiagnosticEvidence evidence,
        LocalRunResult result,
        TextWriter output,
        TextWriter error)
    {
        output.WriteLine("process : " + result.ProcessId);
        output.WriteLine("status  : " + result.Status);
        output.WriteLine("elapsed : " + result.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms");

        var recorded = evidence.Error;
        var recordedFailure = recorded is not null
            && (recorded.ErrorCode is not null || recorded.NativeExitCode is not null || !string.IsNullOrWhiteSpace(recorded.Message));
        if (!recordedFailure)
        {
            if (!result.Succeeded)
            {
                error.WriteLine("error   : recorded success was not reproduced: local run failed: " + Cap(result.ErrorMessage));
                return 1;
            }

            output.WriteLine("outcome : reproduced recorded success");
            return 0;
        }

        if (result.Succeeded)
        {
            error.WriteLine("error   : recorded failure was not reproduced: local run succeeded.");
            return 1;
        }

        var mismatches = new List<string>();
        if (recorded!.NativeExitCode is int expectedExit)
        {
            var observed = ObserveNativeExit(result);
            if (observed is null)
            {
                mismatches.Add("native exit code expected " + expectedExit.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", actual missing");
            }
            else if (observed != expectedExit)
            {
                mismatches.Add(
                    "native exit code expected " + expectedExit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", actual " + observed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (!string.IsNullOrEmpty(recorded.ErrorCode)
            && (result.ErrorMessage is null || !result.ErrorMessage.Contains(recorded.ErrorCode, StringComparison.Ordinal)))
        {
            mismatches.Add("error code '" + recorded.ErrorCode + "' was not reproduced; local error was '" + Cap(result.ErrorMessage) + "'");
        }

        if (recorded.NativeExitCode is null && string.IsNullOrEmpty(recorded.ErrorCode))
        {
            var local = result.ErrorMessage ?? string.Empty;
            if (!local.Contains(recorded.Message, StringComparison.Ordinal)
                && !recorded.Message.Contains(local, StringComparison.Ordinal))
            {
                mismatches.Add("recorded failure message was not reproduced; local error was '" + Cap(local) + "'");
            }
        }

        if (mismatches.Count > 0)
        {
            error.WriteLine("error   : recorded failure was not reproduced:");
            foreach (var mismatch in mismatches)
            {
                error.WriteLine("          " + mismatch);
            }

            return 1;
        }

        // The process failed, as recorded. The command itself succeeded only because that
        // failure is the recorded outcome — status stays Failed.
        output.WriteLine("outcome : reproduced recorded failure");
        if (!string.IsNullOrEmpty(recorded.ErrorCode))
        {
            output.WriteLine("code    : " + recorded.ErrorCode);
        }

        if (recorded.NativeExitCode is int nativeExit)
        {
            output.WriteLine("native  : " + nativeExit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        output.WriteLine("detail  : " + Cap(result.ErrorMessage));
        return 0;
    }

    private static int? ObserveNativeExit(LocalRunResult result)
    {
        int? found = MatchExit(result.ErrorMessage);
        foreach (var log in result.Logs)
        {
            var next = MatchExit(log.Message);
            if (next is not null)
            {
                found = next;
            }
        }

        return found;
    }

    private static int? MatchExit(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = NativeExitCodePattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups["code"].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code))
        {
            return null;
        }

        return code;
    }

    private static async Task WriteOutputsAsync(string root, LocalRunResult result, CancellationToken cancellationToken)
    {
        var output = Path.Join(root, "output");
        for (var i = 0; i < result.Artifacts.Count; i++)
        {
            var bytes = DecodeArtifact(result.Artifacts[i]);
            await File.WriteAllBytesAsync(Path.Join(output, "artifact-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), bytes, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static byte[] DecodeArtifact(string artifact)
    {
        var comma = artifact.IndexOf(',', StringComparison.Ordinal);
        if (artifact.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
        {
            try
            {
                return Convert.FromBase64String(artifact[(comma + 1)..]);
            }
            catch (FormatException)
            {
                return Encoding.UTF8.GetBytes(artifact);
            }
        }

        return Encoding.UTF8.GetBytes(artifact);
    }

    /// <summary>
    /// Turns exported input bytes into the string the executor reads.
    /// A document or data-URI parameter stays UTF-8 even when the media type is
    /// <c>application/geo+json</c>. A parameter the executor base64-decodes stays
    /// base64 even when the media type is <c>application/json</c>. Every other
    /// parameter follows the media type, so a text/plain WKB value is not re-encoded.
    /// </summary>
    internal static string ToParameter(string processId, GpDiagnosticInput input, byte[] bytes)
    {
        var parameter = ProcessCatalog.GetProcess(processId)?.Parameters
            .FirstOrDefault(candidate => string.Equals(candidate.Name, input.Id, StringComparison.Ordinal));
        if (parameter is not null && IsDocumentParameter(parameter))
        {
            return DecodeTextOrBase64(bytes);
        }

        if (parameter is not null && IsBase64Payload(parameter))
        {
            return Convert.ToBase64String(bytes);
        }

        return FromMediaType(input, bytes);
    }

    private static bool IsDocumentParameter(ProcessParameterSpec parameter)
        => parameter.AcceptsGeoJsonDataUri
            || string.Equals(parameter.Name, "inline", StringComparison.Ordinal);

    private static bool IsBase64Payload(ProcessParameterSpec parameter)
        => parameter.ValueType == ProcessParameterValueType.Text
            && (parameter.AcceptsRasterSource
                || (parameter.Description.Contains("base64-encoded", StringComparison.Ordinal)
                    && !parameter.Description.Contains("data:", StringComparison.Ordinal)));

    private static string FromMediaType(GpDiagnosticInput input, byte[] bytes)
    {
        var text = input.MediaType is null
            || input.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        if (!text)
        {
            return Convert.ToBase64String(bytes);
        }

        return DecodeTextOrBase64(bytes);
    }

    private static string DecodeTextOrBase64(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Binary payload declared as text: pass the base64 the executors expect for WKB.
            return Convert.ToBase64String(bytes);
        }
    }

    private static bool TryResolveProcess(
        GpDiagnosticEvidence evidence,
        string? requested,
        out string processId,
        out string? error)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            processId = requested.Trim();
            error = null;
            return true;
        }

        var found = new List<string>();
        foreach (var dependency in evidence.Dependencies)
        {
            Consider(found, dependency.Name);
        }

        Consider(found, evidence.Runtime?.Entrypoint);
        Consider(found, evidence.Release?.ProcessRelease);
        if (found.Count == 1)
        {
            processId = found[0];
            error = null;
            return true;
        }

        processId = string.Empty;
        error = found.Count == 0
            ? "reproduction is incomplete: the evidence names no process id. Pass --process <id>."
            : "reproduction is incomplete: the evidence names more than one process id ("
                + string.Join(", ", found) + "). Pass --process <id>.";
        return false;
    }

    private static void Consider(List<string> found, string? name)
    {
        if (name is null || !LooksLikeProcessId(name) || found.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        found.Add(name);
    }

    private static bool LooksLikeProcessId(string name)
    {
        if (name.Length is 0 or > 128 || !name.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExportedInputsContainCredential(GpDiagnosticEvidence evidence)
    {
        var included = evidence.InputExport?.Included;
        if (included is null)
        {
            return false;
        }

        foreach (var item in included)
        {
            if (string.IsNullOrEmpty(item.ContentBase64))
            {
                continue;
            }

            try
            {
                if (ContainsCredential(Convert.FromBase64String(item.ContentBase64)))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Hash validation reports the bad payload. It is not credential material.
            }
        }

        return false;
    }

    private static bool ContainsCredential(byte[] bytes)
        => GpDiagnosticRedactor.RedactContent(bytes).RedactionApplied;

    private static void RefuseCredentials(TextWriter error)
        => error.WriteLine(
            "refusing to reproduce: the bundle or an exported input contains credential material (userinfo URL, token, or connection string).");

    private static string? CommitSha(string? sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return null;
        }

        var trimmed = sourceRef.Trim();
        if (trimmed.Length is not 40 and not 64)
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return null;
            }
        }

        return trimmed.ToLowerInvariant();
    }

    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        const string prefix = "sha256:";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        if (trimmed.Length != 64)
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return null;
            }
        }

        return trimmed.ToLowerInvariant();
    }

    private static string? FindCheckout(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var git = Path.Join(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? GitHead(string checkout)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = checkout,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("HEAD");
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var head = stdout.Trim();
            return head.Length is 40 or 64 ? head.ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string Gap(string kind, string id, string? expected, string actual)
        => kind + " '" + id + "' expected sha256 " + (expected ?? "unspecified") + ", actual " + actual;

    private static string SafeFileName(string id)
    {
        if (id.Length is > 0 and <= 128 && id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            return id;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
    }

    private static string Cap(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 500 ? value : value[..500];
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new GpCliUsageException($"Option '{option}' requires a value.");
        }

        return args[++index];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The reproduction already finished or failed. Leaving a temp directory is not a credential leak.
        }
    }

    [GeneratedRegex(@"exited with code (?<code>-?\d+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex NativeExitCodePattern();

    private readonly record struct ReproduceArgs(string BundlePath, string? LockPath, string? Checkout, string? ProcessId);
}
