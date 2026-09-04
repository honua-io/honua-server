#!/usr/bin/env python3
"""Compile bounded probes from the checkout's actual configuration/middleware sources.
No server build, external services, or real credentials. Requires .NET 10 on PATH.
"""
import os
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
helper = (root / "src/Honua.Server/Startup/StartupConfigurationHelpers.cs").read_text()
start = helper.index("    public static void AddSecurityConfiguration(")
end = helper.index("    /// <summary>", start)
method = helper[start:end]
runner = (root / "src/Honua.Db/Postgres/Features/Infrastructure/Migrations/PostgresDatabaseMigrationRunner.cs").read_text()
gate_start = runner.index("    private InvalidOperationException? TryBuildContractGateRejection(")
gate_end = runner.index("    /// <summary>", gate_start)
gate = runner[gate_start:gate_end].replace("private InvalidOperationException?", "public InvalidOperationException?", 1)
with tempfile.TemporaryDirectory(prefix="honua-packaging-probe-") as directory:
    probe = Path(directory)
    (probe / "Probe.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
</Project>''')
    (probe / "Helpers.cs").write_text("using Microsoft.Extensions.Configuration;\nusing Microsoft.Extensions.Configuration.Json;\nusing Microsoft.Extensions.Hosting;\nstatic class Helpers {\n" + method + "}\n")
    (probe / "Gate.cs").write_text("using Honua.Core.Configuration;\nusing Honua.Core.Features.Infrastructure.Migrations;\nsealed class GateProbe(MigrationSafetyOptions options) {\nprivate readonly MigrationSafetyOptions _safetyOptions = options;\nprivate readonly string? _contractApprovalToken = null;\n" + gate + "}\n")
    for name, source in {
        "MigrationSafetyOptions.cs": "src/Honua.Core/Configuration/MigrationSafetyOptions.cs",
        "MigrationSafetyClassifier.cs": "src/Honua.Core/Features/Infrastructure/Migrations/MigrationSafetyClassifier.cs",
        "SecretReferenceResolver.cs": "src/Honua.Hosting/Features/Helpers/SecretReferenceResolver.cs",
        "HostValidationMiddleware.cs": "src/Honua.Hosting/Features/Middleware/HostValidationMiddleware.cs",
    }.items():
        (probe / name).write_text((root / source).read_text())
    (probe / "Stubs.cs").write_text('''global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
namespace Honua.Infrastructure.Models {
    // Only the response renderer is stubbed; the complete host decision is production source.
    internal static class StandardErrorHelpers {
        public static IResult CreateBadRequest(HttpContext c, string message) => new Rejected();
        sealed class Rejected : IResult {
            public Task ExecuteAsync(HttpContext c) { c.Response.StatusCode = 400; return Task.CompletedTask; }
        }
    }
}
namespace Honua.Infrastructure.Middleware {
    internal static class HostValidationLog {
        public static void RejectedUntrustedHost(ILogger logger, string? host, string? path) { }
    }
}
''')
    (probe / "Program.cs").write_text('''using System.Net;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

var directory = AppContext.BaseDirectory;
File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{}");
File.WriteAllText(Path.Combine(directory, "appsettings.Production.json"), "{}");
Environment.SetEnvironmentVariable("PACKAGING_PROBE_REDIS", "localhost:6379");
Environment.SetEnvironmentVariable("PACKAGING_PROBE_ConnectionStrings__redis", "env:PACKAGING_PROBE_REDIS");
var configuration = new ConfigurationManager();
configuration.SetBasePath(directory);
configuration.AddJsonFile("appsettings.json");
configuration.AddJsonFile("appsettings.Production.json");
configuration.AddEnvironmentVariables("PACKAGING_PROBE_");
var key = "ConnectionStrings:redis";
configuration[key] = SecretReferenceResolver.ResolveEnvironmentReference(configuration[key], key);
Console.WriteLine($"Redis before AddSecurityConfiguration: {configuration[key]}");
Helpers.AddSecurityConfiguration(configuration, new ProductionEnvironment());
Console.WriteLine($"Redis after AddSecurityConfiguration: {configuration[key]}");
var secretPassed = configuration[key] == "localhost:6379";
Console.WriteLine($"Secret survives source ordering: {(secretPassed ? "PASS" : "FAIL")}");

var hosts = new ConfigurationManager();
hosts["AllowedHosts"] = ""; // shipped appsettings.Production.json
async Task<int> Request(string path) {
    var middleware = new HostValidationMiddleware(c => { c.Response.StatusCode = 204; return Task.CompletedTask; },
        hosts, new ProductionEnvironment(), NullLogger<HostValidationMiddleware>.Instance);
    var context = new DefaultHttpContext();
    context.Request.Path = path;
    context.Request.Host = new HostString("honua.example.com");
    context.Connection.LocalIpAddress = IPAddress.Parse("172.19.0.2");
    context.Connection.RemoteIpAddress = IPAddress.Parse("172.19.0.1");
    await middleware.InvokeAsync(context);
    return context.Response.StatusCode;
}
var initial = await Request("/api/v1/admin/config");
Console.WriteLine($"Documented production proxy Host honua.example.com: HTTP {initial}");
Console.WriteLine($"Same Host health readiness bypass: HTTP {await Request("/healthz/ready")}");
hosts["PUBLIC_BASE_URL"] = "https://honua.example.com";
var corrected = await Request("/api/v1/admin/config");
Console.WriteLine($"With explicit PUBLIC_BASE_URL: HTTP {corrected}");
Console.WriteLine($"Documented production host accepted: {(initial == 204 ? "PASS" : "FAIL")}");
var policies = new ConfigurationManager();
policies["Database:MigrationSafety:ContractApplyPolicy"] = "2";
var services = new ServiceCollection();
services.AddOptions<MigrationSafetyOptions>().Bind(policies.GetSection(MigrationSafetyOptions.SectionName));
using var provider = services.BuildServiceProvider();
var options = provider.GetRequiredService<IOptions<MigrationSafetyOptions>>().Value;
var classified = new[] { MigrationSafetyClassifier.Classify("002_RemoveOldColumn.sql", "-- honua:compatibility-review reason=reviewed expansion cleanup\\nALTER TABLE example DROP COLUMN legacy;") };
Console.WriteLine($"Default contract policy requires approval: {new GateProbe(new()).TryBuildContractGateRejection(classified, true) is not null}");
Console.WriteLine($"Bound numeric contract policy: {(int)options.ContractApplyPolicy}; defined: {Enum.IsDefined(options.ContractApplyPolicy)}");
var policyBlocked = new GateProbe(options).TryBuildContractGateRejection(classified, true) is not null;
Console.WriteLine($"Undefined contract policy requires approval: {policyBlocked}");
Console.WriteLine($"Invalid migration policy fails closed: {(policyBlocked ? "PASS" : "FAIL")}");
Environment.ExitCode = secretPassed && initial == 204 && policyBlocked ? 0 : 1;

sealed class ProductionEnvironment : IHostEnvironment {
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "Probe";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
''')
    env = dict(os.environ, HONUA_MSBUILD_NODE_CAP="4")
    result = subprocess.run(["dotnet", "run", "--project", str(probe / "Probe.csproj"), "--verbosity", "quiet"], env=env, timeout=180)
    raise SystemExit(result.returncode)
