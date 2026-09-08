# Registry clients and package credentials

The [customer install manifest](https://honua.io/data/customer-install-manifest.json)
records the pins and their origins. The quickstart uses the public PyPI control
and data clients; neither those packages nor the public server image requires
GitHub authentication.

| Registry | Manifest pin | Role | Package-read credentials |
| --- | --- | --- | --- |
| PyPI | `honua-admin==0.1.8` | Control plane used by this journey | None |
| PyPI | `honua-sdk==0.1.11` | Data plane used by this journey | None |
| PyPI | `mcp==2.1.1` | Transport for the server MCP import tool | None |
| npm | `@honua/sdk-js@0.1.9-beta.0` | Alternative JS SDK and CLI | None |
| npm | `@honua/mcp-server@0.1.4-beta.0` | Alternative MCP proxy | None |
| GitHub Packages NuGet | `Honua.Sdk` `1.6.0` | Alternative .NET SDK | GitHub account with package access and a classic PAT with `read:packages` |

npm and NuGet versions are the release manifest's existing alternative-client
pins, not a claim that this Python journey rehearsed those clients. Do not
install all ecosystems to complete the quickstart. The publication record beside
the manifest identifies its immutable source and byte hash.

## Optional NuGet credential setup in PowerShell

Use this only for the GitHub Packages feed. Install the .NET SDK first. A classic
PAT may also need organization SSO authorization. Enter it at the secure prompt;
it is kept in the current process environment for NuGet, not committed in a config
file. The configuration is scoped to a new sample project. NuGet.org remains
available for public dependencies; Honua packages are mapped to GitHub Packages.

```powershell
$ErrorActionPreference = 'Stop'
$Sample = 'honua-dotnet-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
New-Item -ItemType Directory -Path $Sample | Out-Null
Set-Location -LiteralPath $Sample
dotnet new console
if ($LASTEXITCODE -ne 0) { throw 'Sample project creation failed' }
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="honua" value="https://nuget.pkg.github.com/honua-io/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="honua"><package pattern="Honua.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
'@ | Set-Content -LiteralPath NuGet.Config -Encoding UTF8
$Login = Read-Host 'GitHub login with package access'
$Token = Read-Host 'Classic PAT with read:packages' -AsSecureString
$Pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Token)
try {
    $env:NuGetPackageSourceCredentials_honua = 'Username=' + $Login + ';Password=' + [Runtime.InteropServices.Marshal]::PtrToStringBSTR($Pointer) + ';ValidAuthenticationTypes=Basic'
    dotnet add package Honua.Sdk --version 1.6.0
    if ($LASTEXITCODE -ne 0) { throw 'NuGet package restore failed; check package access and SSO' }
} finally {
    Remove-Item Env:NuGetPackageSourceCredentials_honua -ErrorAction SilentlyContinue
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($Pointer)
    $Token.Dispose()
}
```

Return to the [quickstart](quickstart.md) for the Python installation journey.
