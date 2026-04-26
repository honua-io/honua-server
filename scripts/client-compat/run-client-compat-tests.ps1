# Client Compatibility Certification — Windows Desktop App Validation
#
# Generates pre-configured connection/project files for each desktop client,
# launches the apps, and prompts you to confirm pass/fail.
#
# Prerequisites:
#   1. Start server in WSL:  ./scripts/client-compat/client-compat-server.sh
#   2. Run this from Windows PowerShell
#
# Usage:
#   .\scripts\client-compat\run-client-compat-tests.ps1
#   .\scripts\client-compat\run-client-compat-tests.ps1 -BaseUrl http://localhost:8080
#   .\scripts\client-compat\run-client-compat-tests.ps1 -Client qgis

param(
    [string]$BaseUrl = $env:HONUA_CLIENT_COMPAT_BASE_URL,
    [string]$Service = "compat",
    [string]$OutputDir = "client-compat-results",
    [ValidateSet("all", "qgis", "arcgis-pro", "excel", "powerbi")]
    [string]$Client = "all"
)

if (-not $BaseUrl) { $BaseUrl = "http://localhost:8080" }

$results = @()
$ConnFilesDir = Join-Path $OutputDir "connections"

Write-Host ""
Write-Host "Client Compatibility Certification" -ForegroundColor Blue
Write-Host "====================================" -ForegroundColor Blue
Write-Host "  Server:  $BaseUrl"
Write-Host "  Service: $Service"
Write-Host ""

# ── Verify server ───────────────────────────────────────────────────────────
try {
    Invoke-WebRequest -Uri "$BaseUrl/healthz/ready" -Method Get -TimeoutSec 10 -ErrorAction Stop | Out-Null
    Write-Host "  Server is reachable" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: Server at $BaseUrl is not reachable." -ForegroundColor Red
    Write-Host "  Start it in WSL first: ./scripts/client-compat/client-compat-server.sh" -ForegroundColor Yellow
    exit 2
}

# ── Prepare output ──────────────────────────────────────────────────────────
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $ConnFilesDir -Force | Out-Null

# ── Prompt helper ───────────────────────────────────────────────────────────
function Prompt-PassFail {
    param([string]$ClientName, [string[]]$Checks)
    Write-Host ""
    Write-Host "  Verify in $ClientName :" -ForegroundColor Cyan
    $i = 1
    foreach ($check in $Checks) {
        Write-Host "    $i. $check"
        $i++
    }
    Write-Host ""
    $answer = Read-Host "  Did $ClientName pass all checks? (y/n/s=skip)"
    $answer = $answer.Trim().ToLower()
    if ($answer -eq "s") {
        return "skipped"
    }
    return ($answer -eq "y" -or $answer -eq "yes")
}

function Add-Result {
    param([string]$ClientName, $Passed, [string]$Notes = "")
    if ($Passed -eq "skipped") {
        $script:results += @{ client = $ClientName; passed = $null; notes = "Skipped" }
        Write-Host "  [$ClientName] SKIPPED" -ForegroundColor Yellow
    }
    elseif ($Passed) {
        $script:results += @{ client = $ClientName; passed = $true; notes = $Notes }
        Write-Host "  [$ClientName] PASS" -ForegroundColor Green
    }
    else {
        $script:results += @{ client = $ClientName; passed = $false; notes = $Notes }
        Write-Host "  [$ClientName] FAIL" -ForegroundColor Red
    }
}

# ═══════════════════════════════════════════════════════════════════════════
# QGIS
# ═══════════════════════════════════════════════════════════════════════════
if ($Client -eq "all" -or $Client -eq "qgis") {
    Write-Host ""
    Write-Host "-------------------------------------------" -ForegroundColor DarkGray
    Write-Host "QGIS" -ForegroundColor Cyan

    # Generate .qgs project file with WMS + OGC API Features layers
    $qgsPath = Join-Path $ConnFilesDir "honua-compat.qgs"
    $wmsUrl = "$BaseUrl/rest/services/$Service/MapServer/WMS"
    $ogcUrl = "$BaseUrl/ogc/features/collections/${Service}:Cities/items"

    $qgsXml = @"
<!DOCTYPE qgis PUBLIC 'http://mrcc.com/qgis.dtd' 'SYSTEM'>
<qgis projectname="Honua Client Compat" version="3.34.0">
  <title>Honua Client Compatibility Test</title>
  <projectlayers>
    <maplayer type="raster" name="Cities (WMS)" geometry="No geometry" id="wms_cities">
      <datasource>contextualWMSLegend=0&amp;crs=EPSG:4326&amp;dpiMode=7&amp;featureCount=10&amp;format=image/png&amp;layers=0&amp;styles=&amp;url=$([System.Security.SecurityElement]::Escape($wmsUrl))</datasource>
      <layername>Cities (WMS)</layername>
      <provider encoding="">wms</provider>
      <srs>
        <spatialrefsys>
          <authid>EPSG:4326</authid>
        </spatialrefsys>
      </srs>
    </maplayer>
    <maplayer type="raster" name="Rivers (WMS)" geometry="No geometry" id="wms_rivers">
      <datasource>contextualWMSLegend=0&amp;crs=EPSG:4326&amp;dpiMode=7&amp;featureCount=10&amp;format=image/png&amp;layers=1&amp;styles=&amp;url=$([System.Security.SecurityElement]::Escape($wmsUrl))</datasource>
      <layername>Rivers (WMS)</layername>
      <provider encoding="">wms</provider>
      <srs>
        <spatialrefsys>
          <authid>EPSG:4326</authid>
        </spatialrefsys>
      </srs>
    </maplayer>
    <maplayer type="raster" name="Counties (WMS)" geometry="No geometry" id="wms_counties">
      <datasource>contextualWMSLegend=0&amp;crs=EPSG:4326&amp;dpiMode=7&amp;featureCount=10&amp;format=image/png&amp;layers=2&amp;styles=&amp;url=$([System.Security.SecurityElement]::Escape($wmsUrl))</datasource>
      <layername>Counties (WMS)</layername>
      <provider encoding="">wms</provider>
      <srs>
        <spatialrefsys>
          <authid>EPSG:4326</authid>
        </spatialrefsys>
      </srs>
    </maplayer>
    <maplayer type="vector" name="Cities (OGC Features)" geometry="Point" id="ogc_cities">
      <datasource>$([System.Security.SecurityElement]::Escape($ogcUrl))</datasource>
      <layername>Cities (OGC API Features)</layername>
      <provider encoding="UTF-8">oapif</provider>
      <srs>
        <spatialrefsys>
          <authid>EPSG:4326</authid>
        </spatialrefsys>
      </srs>
    </maplayer>
  </projectlayers>
</qgis>
"@
    $qgsXml | Set-Content -Path $qgsPath -Encoding UTF8
    Write-Host "  Generated: $qgsPath"

    # Find QGIS
    $qgisExe = @(
        "C:\Program Files\QGIS 3.40\bin\qgis-bin.exe",
        "C:\Program Files\QGIS 3.38\bin\qgis-bin.exe",
        "C:\Program Files\QGIS 3.36\bin\qgis-bin.exe",
        "C:\Program Files\QGIS 3.34\bin\qgis-bin.exe",
        "C:\OSGeo4W\bin\qgis-bin.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($qgisExe) {
        Write-Host "  Launching QGIS with project file..." -ForegroundColor Yellow
        Start-Process -FilePath $qgisExe -ArgumentList "--project", "`"$((Resolve-Path $qgsPath).Path)`""
    }
    else {
        Write-Host "  QGIS not found in standard paths." -ForegroundColor Yellow
        Write-Host "  Open QGIS manually and load: $((Resolve-Path $qgsPath).Path)" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "  Connection URLs:" -ForegroundColor DarkGray
    Write-Host "    WMS:          $wmsUrl"
    Write-Host "    WMTS:         $BaseUrl/rest/services/$Service/MapServer/WMTS"
    Write-Host "    OGC Features: $BaseUrl/ogc/features"

    $passed = Prompt-PassFail "QGIS" @(
        "WMS layers (Cities, Rivers, Counties) render on the map canvas"
        "OGC API Features layer (Cities) shows point features"
        "Pan/zoom works without errors"
        "Identify tool returns attributes for a city feature"
    )
    Add-Result "QGIS" $passed
}

# ═══════════════════════════════════════════════════════════════════════════
# ArcGIS Pro
# ═══════════════════════════════════════════════════════════════════════════
if ($Client -eq "all" -or $Client -eq "arcgis-pro") {
    Write-Host ""
    Write-Host "-------------------------------------------" -ForegroundColor DarkGray
    Write-Host "ArcGIS Pro" -ForegroundColor Cyan

    # Generate ArcPy script for Pro's Python window
    $arcpyScript = Join-Path $ConnFilesDir "add-honua-layers.py"
    $fsUrl = "$BaseUrl/rest/services/$Service/FeatureServer"

    $arcpyContent = @"
# Paste this into ArcGIS Pro's Python window (View > Python)
# It adds the Honua compat FeatureServer layers to the current map.

import arcpy

aprx = arcpy.mp.ArcGISProject("CURRENT")
m = aprx.listMaps()[0]

layers = [
    "$fsUrl/0",  # Cities (Point, 1200 features)
    "$fsUrl/1",  # Rivers (LineString)
    "$fsUrl/2",  # Counties (Polygon)
]

for url in layers:
    try:
        m.addDataFromPath(url)
        print(f"Added: {url}")
    except Exception as e:
        print(f"Failed: {url} - {e}")

print("Done. Check the Contents pane for the new layers.")
"@
    $arcpyContent | Set-Content -Path $arcpyScript -Encoding UTF8
    Write-Host "  Generated: $arcpyScript"

    # Find ArcGIS Pro
    $proExe = @(
        "C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($proExe) {
        Write-Host "  Launching ArcGIS Pro..." -ForegroundColor Yellow
        Start-Process -FilePath $proExe
    }
    else {
        Write-Host "  ArcGIS Pro not found. Open it manually." -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "  Steps:" -ForegroundColor DarkGray
    Write-Host "    1. Create a new Map project in ArcGIS Pro"
    Write-Host "    2. Open Python window (View > Python)"
    Write-Host "    3. Paste contents of: $((Resolve-Path $arcpyScript).Path)"
    Write-Host "    4. Or: Add Data > From Path > $fsUrl/0"
    Write-Host ""
    Write-Host "  Connection URLs:" -ForegroundColor DarkGray
    Write-Host "    FeatureServer: $fsUrl"
    Write-Host "    MapServer:     $BaseUrl/rest/services/$Service/MapServer"

    $passed = Prompt-PassFail "ArcGIS Pro" @(
        "FeatureServer layers appear in Contents pane"
        "Cities (Point), Rivers (Line), Counties (Polygon) render on map"
        "Attribute table shows data (right-click layer > Attribute Table)"
        "Definition query filters correctly (e.g., population > 500000)"
    )
    Add-Result "ArcGIS Pro" $passed
}

# ═══════════════════════════════════════════════════════════════════════════
# Excel (OData via Power Query)
# ═══════════════════════════════════════════════════════════════════════════
if ($Client -eq "all" -or $Client -eq "excel") {
    Write-Host ""
    Write-Host "-------------------------------------------" -ForegroundColor DarkGray
    Write-Host "Excel" -ForegroundColor Cyan

    $odataUrl = "$BaseUrl/odata"

    # Generate .odc connection file
    $odcPath = Join-Path $ConnFilesDir "honua-compat-odata.odc"
    $odcContent = @"
<html xmlns:o="urn:schemas-microsoft-com:office:office"
xmlns="http://www.w3.org/TR/REC-html40">
<head>
<meta http-equiv=Content-Type content="text/x-ms-odc; charset=utf-8">
<meta name=ProgId content=ODC.Database>
<meta name=SourceType content=OLEDB>
<xml id=docprops></xml>
<xml id=msodc>
 <odc:OfficeDataConnection
  xmlns:odc="urn:schemas-microsoft-com:office:odc"
  xmlns="http://www.w3.org/TR/REC-html40">
  <odc:ConnectionString>Data Source=$odataUrl</odc:ConnectionString>
  <odc:CommandType>Table</odc:CommandType>
  <odc:CommandText>${Service}_Cities</odc:CommandText>
 </odc:OfficeDataConnection>
</xml>
</head>
</html>
"@
    $odcContent | Set-Content -Path $odcPath -Encoding UTF8
    Write-Host "  Generated: $odcPath"

    # Try to launch Excel
    $excelExe = @(
        "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
        "${env:ProgramFiles}\Microsoft Office\root\Office16\EXCEL.EXE"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $excelExe) {
        # Try via COM to find the path
        try {
            $excelApp = New-Object -ComObject Excel.Application -ErrorAction Stop
            $excelExe = (Get-Process -Id $excelApp.Hwnd -ErrorAction SilentlyContinue) | Select-Object -First 1
            $excelApp.Quit()
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excelApp) | Out-Null
        }
        catch { }
    }

    if ($excelExe) {
        Write-Host "  Launching Excel..." -ForegroundColor Yellow
        Start-Process -FilePath $excelExe
    }
    else {
        Write-Host "  Opening .odc file (should launch Excel)..." -ForegroundColor Yellow
        Start-Process (Resolve-Path $odcPath).Path
    }

    Write-Host ""
    Write-Host "  Steps:" -ForegroundColor DarkGray
    Write-Host "    1. In Excel: Data > Get Data > From OData Feed"
    Write-Host "    2. URL: $odataUrl"
    Write-Host "    3. Select '${Service}_Cities' table > Load"
    Write-Host "    4. Verify 1200+ rows load"
    Write-Host "    5. Edit a cell value"
    Write-Host "    6. Try Data > Refresh All"
    Write-Host ""
    Write-Host "  Connection URLs:" -ForegroundColor DarkGray
    Write-Host "    OData:     $odataUrl"
    Write-Host "    `$metadata: $odataUrl/`$metadata"

    $passed = Prompt-PassFail "Excel" @(
        "Power Query connects to OData feed without errors"
        "Cities table loads with 1200+ rows"
        "Columns include: name, population, state, country, etc."
        "Data > Refresh All reloads data successfully"
    )
    Add-Result "Excel" $passed
}

# ═══════════════════════════════════════════════════════════════════════════
# Power BI Desktop
# ═══════════════════════════════════════════════════════════════════════════
if ($Client -eq "all" -or $Client -eq "powerbi") {
    Write-Host ""
    Write-Host "-------------------------------------------" -ForegroundColor DarkGray
    Write-Host "Power BI Desktop" -ForegroundColor Cyan

    # Generate .pbids file
    $pbidsPath = Join-Path $ConnFilesDir "honua-compat-odata.pbids"
    $odataUrl = "$BaseUrl/odata"
    $pbidsContent = @{
        version     = "0.1"
        connections = @(
            @{
                details = @{
                    protocol = "odata"
                    address  = @{
                        url = $odataUrl
                    }
                }
            }
        )
    } | ConvertTo-Json -Depth 5
    $pbidsContent | Set-Content -Path $pbidsPath -Encoding UTF8
    Write-Host "  Generated: $pbidsPath"

    # Find Power BI Desktop
    $pbiExe = @(
        "$env:ProgramFiles\Microsoft Power BI Desktop\bin\PBIDesktop.exe",
        "${env:ProgramFiles(x86)}\Microsoft Power BI Desktop\bin\PBIDesktop.exe",
        "$env:LOCALAPPDATA\Microsoft\WindowsApps\PBIDesktop.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($pbiExe) {
        Write-Host "  Launching Power BI Desktop with .pbids file..." -ForegroundColor Yellow
        Start-Process -FilePath $pbiExe -ArgumentList "`"$((Resolve-Path $pbidsPath).Path)`""
    }
    else {
        Write-Host "  Opening .pbids file (should launch Power BI)..." -ForegroundColor Yellow
        Start-Process (Resolve-Path $pbidsPath).Path
    }

    Write-Host ""
    Write-Host "  The .pbids file should open PBI's Navigator to $odataUrl"
    Write-Host ""
    Write-Host "  Steps:" -ForegroundColor DarkGray
    Write-Host "    1. In Navigator, select '${Service}_Cities' > Load"
    Write-Host "    2. Check that 1200+ rows appear in the data model"
    Write-Host "    3. Add a Map visual, drag a geographic field"
    Write-Host "    4. Add a Table visual with name + population"

    $passed = Prompt-PassFail "Power BI" @(
        "Navigator shows entity sets from the OData feed"
        "Cities table loads with 1200+ rows"
        "Table visual displays name and population columns"
        "Map visual renders geographic data (if applicable)"
    )
    Add-Result "Power BI" $passed
}

# ═══════════════════════════════════════════════════════════════════════════
# Summary
# ═══════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "====================================" -ForegroundColor Blue
Write-Host "Summary" -ForegroundColor Blue
Write-Host "====================================" -ForegroundColor Blue

$summaryLines = @(
    "# Client Compatibility Certification Results",
    "",
    "Server: $BaseUrl",
    "Service: $Service",
    "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "",
    "| Client | Result |",
    "|--------|--------|"
)

foreach ($r in $results) {
    $statusText = if ($null -eq $r.passed) { "SKIPPED" } elseif ($r.passed) { "PASS" } else { "FAIL" }
    $color = if ($null -eq $r.passed) { "Yellow" } elseif ($r.passed) { "Green" } else { "Red" }
    Write-Host "  $($r.client.PadRight(15)) $statusText" -ForegroundColor $color
    $summaryLines += "| $($r.client) | $statusText |"
}

$summaryLines += @("", "## Connection Files", "", "Generated in ``$ConnFilesDir/``")
$summaryLines -join "`n" | Set-Content -Path (Join-Path $OutputDir "compatibility-summary.md") -Encoding UTF8

$jsonResult = @{
    generated = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
    base_url  = $BaseUrl
    service   = $Service
    results   = $results
} | ConvertTo-Json -Depth 5
$jsonResult | Set-Content -Path (Join-Path $OutputDir "compatibility-matrix.json") -Encoding UTF8

Write-Host ""
Write-Host "Results saved to $OutputDir\" -ForegroundColor Cyan
Write-Host "Connection files in $ConnFilesDir\" -ForegroundColor Cyan
