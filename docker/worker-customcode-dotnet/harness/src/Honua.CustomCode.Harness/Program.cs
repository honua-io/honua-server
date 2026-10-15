// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Harness;

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

var options = new HarnessOptions
{
    SourceRoot = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_SOURCE_ROOT") ?? "/work/src",
    BuildOutput = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_BUILD_OUTPUT") ?? "/work/build",
    WorkDir = Environment.GetEnvironmentVariable("HONUA_CUSTOMCODE_WORKDIR") ?? "/work/out",
};

var harness = new Harness(options);
return await harness.RunAsync(cancellationToken: cts.Token).ConfigureAwait(false);
