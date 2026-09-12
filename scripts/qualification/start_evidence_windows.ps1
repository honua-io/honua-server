[CmdletBinding()]
param([switch]$NoBuild, [switch]$ReuseContainers)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$artifacts = Join-Path $repo 'TestResults/evidence-live'
New-Item -ItemType Directory -Force $artifacts | Out-Null
if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'src/Honua.Server/Honua.Server.csproj') --configuration Release -maxcpucount:4
    if ($LASTEXITCODE -ne 0) { throw 'Native server build failed.' }
}
$assembly = Join-Path $repo 'src/Honua.Server/bin/Release/net10.0/Honua.Server.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the native server first.' }

# These names and loopback ports belong exclusively to this disposable harness.
if ($ReuseContainers) {
    foreach ($containerName in @('honua-3475-postgres', 'honua-3475-redis')) {
        $containerJson = docker inspect $containerName
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect $containerName." }
        $container = ($containerJson | ConvertFrom-Json)[0]
        if ($container.Config.Labels.'honua.evidence.issue' -ne '3475' -or -not $container.State.Running) {
            throw "$containerName must be a running container owned by this evidence harness."
        }
    }
} else {
    docker run -d --name honua-3475-postgres --label honua.evidence.issue=3475 -p 127.0.0.1:55475:5432 -e POSTGRES_DB=honua_evidence -e POSTGRES_USER=honua -e POSTGRES_PASSWORD=local-evidence-only postgis/postgis:17-3.5
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Postgres container; existing containers are never replaced.' }
    docker run -d --name honua-3475-redis --label honua.evidence.issue=3475 -p 127.0.0.1:56375:6379 redis:7.4-alpine
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Redis container.' }
}

$settings = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    ConnectionStrings__DefaultConnection = 'Host=localhost;Port=55475;Database=honua_evidence;Username=honua;Password=local-evidence-only'
    ConnectionStrings__Redis = 'localhost:56375'
    HONUA_ADMIN_PASSWORD = 'local-evidence-admin-only'
    Security__ConnectionEncryption__MasterKey = 'honua-operate-fixture-development-master-key-0001'
    Security__ConnectionEncryption__Salt = 'aG9udWEtb3BlcmF0ZS1maXh0dXJlLWRldmVsb3BtZW50LXNhbHQ='
    Kestrel__Endpoints__Http__Url = 'http://127.0.0.1:18475'
    Kestrel__Endpoints__Grpc__Url = 'http://127.0.0.1:18476'
    Alerts__Enabled = 'true'
    'Capabilities__Experimental__alerts.geofence__Enabled' = 'true'
    Licensing__DevGrantEdition = 'Enterprise'
    FileStorage__LocalStorage__BasePath = (Join-Path $artifacts 'storage')
}
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    $server = Start-Process dotnet -ArgumentList ('"' + $assembly + '"') -WindowStyle Hidden -PassThru `
        -WorkingDirectory (Join-Path $repo 'src/Honua.Server') `
        -RedirectStandardOutput (Join-Path $artifacts 'server.log') `
        -RedirectStandardError (Join-Path $artifacts 'server-error.log')
    $server.Id | Set-Content (Join-Path $artifacts 'server.pid')
    Write-Output "Native evidence server PID: $($server.Id)"
} finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
