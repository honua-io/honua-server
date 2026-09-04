# Observability/error contract probes

Base: `f16b248dd7`; tracker honua-io/honua-release#272.
This packet is read-mostly. No runtime implementation files are changed.

Run through the lane `dotnet` shim (four-node cap):

```sh
HONUA_MSBUILD_NODE_CAP=4 dotnet test tests/dotnet/Honua.Observability.Probes/Honua.Observability.Probes.csproj --filter FullyQualifiedName~ObservabilityContractProbes
```

The full project references only the hosting dependency closure, not the server or solution. Its assembly name uses the existing hosting friend-assembly test seam. The fresh dependency build exceeded the 280-second hunt limit before tests executed; the two telemetry tests have not been run here.

The framework-only slice avoids that dependency build and exercises the real ASP.NET request diagnostics with the production category levels copied from Program.cs:

```sh
HONUA_MSBUILD_NODE_CAP=4 dotnet test tests/dotnet/Honua.Observability.Probes/Honua.Observability.Probes.csproj -p:FrameworkOnly=true --filter FullyQualifiedName~HostingDiagnostics
```

`HostingDiagnostics_MustNotLogQueryCredentials` is deliberately red on the base configuration (#4269). The Warning-threshold control demonstrates the proposed configuration mitigation, not a product implementation change. The input uses synthetic token and email markers only.

Observed on .NET 10.0.11: production-threshold regression **1 failed** (2.43 seconds); Warning-threshold control **1 passed** (3.20 seconds). Captured Information events were the real framework `Request starting ...{Path}{QueryString}` and `Request finished ...{Path}{QueryString}` templates. The credential assertion matched the token marker in the former.

`GeoServicesHttp200Error_MustHaveInBandTrue` executes the actual shared formatter and inspects its emitted counter (#4263). `GaClassicRoutes_MustHaveServingProtocol` checks the actual classifier for the registered `/wps` route (#4265).

The Prometheus fixture targets the actual documented alert-rule file with emitted label sets and a 100% failure rate (#4264):

```sh
cd tests/dotnet/Honua.Observability.Probes
promtool test rules prometheus-errors.test.yml
```

Promtool was unavailable in this lane and the fixture was not executed. The verified defect is the rules' default vector division between incompatible label sets. The fixture expects a corrected aggregate ratio to fire the critical alert.
