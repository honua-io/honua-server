// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Harness;

// The image ENTRYPOINT. Reads CUSTOMCODE_* / HONUA_* from the Batch container env (or
// a CUSTOMCODE_JOB_SPEC file), runs one custom-code job, and exits with the terminal
// code. SIGTERM/SIGINT request cooperative cancellation so an in-flight tool can stop.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// Honor SIGTERM (Batch sends it on cancel/timeout) as cooperative cancellation.
using var sigtermRegistration = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    ctx =>
    {
        ctx.Cancel = true;
        cts.Cancel();
    });

var options = new HarnessOptions
{
    SourceRoot = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_SOURCE_ROOT") ?? "/work/src",
    BuildOutput = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_BUILD_OUTPUT") ?? "/work/build",
    WorkDir = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_WORKDIR") ?? "/work/out",
};

var harness = new Harness(options);
return await harness.RunAsync(cancellationToken: cts.Token).ConfigureAwait(false);
