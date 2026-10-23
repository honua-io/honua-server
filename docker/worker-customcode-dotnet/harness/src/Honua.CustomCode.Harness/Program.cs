// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Harness;
using System.Diagnostics;

// The image ENTRYPOINT. Reads CUSTOMCODE_* / HONUA_* from the Batch container env (or
// a CUSTOMCODE_JOB_SPEC file), runs one custom-code job, and exits with the terminal
// code. Ctrl+C, SIGTERM (Batch sends it on cancel/timeout) and process exit request
// cooperative cancellation so an in-flight tool can stop. The registration is declared
// after the source so `using` disposes it first: a late exit event never reaches a
// disposed source (that used to throw after every successful local run).
using var cts = new CancellationTokenSource();
using var lifetime = HarnessLifetime.Register(cts);

if (args.Length > 0 && args[0] == "local")
{
    return await LocalHarness.RunCommandAsync(args[1..], cts.Token).ConfigureAwait(false);
}

// Cloud credentials are forbidden in the container, not merely scrubbed before execution.
// The server independently verifies an exact credential-free Fargate definition. The
// Honua token is a deliberately visible capability bound to one job and attempt.
var allowedAws = new HashSet<string>(StringComparer.Ordinal)
{
    "AWS_REGION", "AWS_DEFAULT_REGION", "AWS_EC2_METADATA_DISABLED", "AWS_EXECUTION_ENV",
    "AWS_BATCH_JOB_ID", "AWS_BATCH_JOB_ATTEMPT", "AWS_BATCH_CE_NAME", "AWS_BATCH_JQ_NAME",
    "AWS_BATCH_JOB_MAIN_NODE_INDEX"
};
foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
{
    var name = (string)entry.Key;
    if ((name.StartsWith("AWS_", StringComparison.Ordinal) && !allowedAws.Contains(name)) ||
        name.StartsWith("AZURE_", StringComparison.Ordinal) || name.StartsWith("GOOGLE_", StringComparison.Ordinal) ||
        name == "CLOUDSDK_CONFIG")
    {
        Console.Error.WriteLine("Custom-code launch refused: infrastructure credential environment is forbidden.");
        return Harness.ExitHarnessError;
    }
}
if (args.Length == 0)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    start.ArgumentList.Add(typeof(Harness).Assembly.Location);
    start.ArgumentList.Add("--credential-free-child");
    start.Environment.Clear();
    var safeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "PATH", "LANG", "LC_ALL", "TZ", "DOTNET_ROOT", "NUGET_PACKAGES", "GDAL_DATA", "PROJ_LIB", "LD_LIBRARY_PATH"
    };
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
    {
        var name = (string)entry.Key;
        if (safeNames.Contains(name) || name.StartsWith("CUSTOMCODE_", StringComparison.Ordinal) || name.StartsWith("HONUA_", StringComparison.Ordinal))
        {
            start.Environment[name] = (string?)entry.Value;
        }
    }
    start.Environment["AWS_EC2_METADATA_DISABLED"] = "true";
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch custom-code child.");
    try
    {
        await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // Batch runs Linux. Forward SIGTERM so the child can close its journal before
        // the bounded grace period expires; forcibly reap an uncooperative tool afterward.
        if (!child.HasExited && OperatingSystem.IsLinux())
        {
            using var signal = Process.Start(new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                ArgumentList = { "-TERM", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            });
            if (signal is not null)
            {
                await signal.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await child.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
        return Harness.ExitCancelled;
    }
    return child.ExitCode;
}
if (args is not ["--credential-free-child"])
{
    Console.Error.WriteLine("Unknown custom-code launch arguments.");
    return Harness.ExitHarnessError;
}

var options = new HarnessOptions
{
    SourceRoot = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_SOURCE_ROOT") ?? "/work/src",
    BuildOutput = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_BUILD_OUTPUT") ?? "/work/build",
    WorkDir = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_WORKDIR") ?? "/work/out",
};

var harness = new Harness(options);
return await harness.RunAsync(cancellationToken: cts.Token).ConfigureAwait(false);
