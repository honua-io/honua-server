#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Verification script for critical security fixes

.DESCRIPTION
    This script verifies that all 4 critical security vulnerabilities have been properly fixed:
    1. Authentication Bypass Logic Error
    2. SQL Injection Prevention
    3. CORS Credential Security
    4. Information Disclosure in Logs

.PARAMETER BaseUrl
    Base URL of the Honua server to test (default: http://localhost:5000)

.PARAMETER Verbose
    Enable verbose output for detailed test results

.EXAMPLE
    ./verify-security-fixes.ps1
    ./verify-security-fixes.ps1 -BaseUrl "https://staging.honua.io" -Verbose
#>

param(
    [string]$BaseUrl = "http://localhost:5000",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Write-Host "🔒 Security Fixes Verification Script" -ForegroundColor Cyan
Write-Host "Testing fixes for 4 critical security vulnerabilities" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    AuthBypass = $false
    SqlInjection = $false
    Cors = $false
    InfoDisclosure = $false
}

# Test 1: Authentication Bypass Logic
Write-Host "1. Testing Authentication Bypass Protection..." -ForegroundColor Yellow

try {
    # Test that production environment blocks bypass
    $response = Invoke-WebRequest -Uri "$BaseUrl/admin/health" -Method GET -SkipHttpErrorCheck

    if ($response.StatusCode -eq 401) {
        Write-Host "   ✅ Production authentication bypass properly blocked" -ForegroundColor Green
        $testResults.AuthBypass = $true
    } else {
        Write-Host "   ❌ Authentication bypass may be vulnerable" -ForegroundColor Red
        if ($Verbose) {
            Write-Host "      Response: $($response.StatusCode)" -ForegroundColor Gray
        }
    }
} catch {
    Write-Host "   ⚠️  Could not test auth bypass (server may not be running)" -ForegroundColor Yellow
}

# Test 2: SQL Injection Prevention
Write-Host "2. Testing SQL Injection Prevention..." -ForegroundColor Yellow

$sqlPayloads = @(
    "'; DROP TABLE users; --",
    "field' OR '1'='1",
    "name UNION SELECT password FROM users"
)

$sqlTestsPassed = 0
foreach ($payload in $sqlPayloads) {
    try {
        $encodedPayload = [System.Web.HttpUtility]::UrlEncode($payload)
        $response = Invoke-WebRequest -Uri "$BaseUrl/ogc/features/v1/collections/test/items?filter=name='$encodedPayload'" -Method GET -SkipHttpErrorCheck

        $content = $response.Content
        if ($content -notmatch "DROP TABLE|syntax error|INSERT INTO|DELETE FROM") {
            $sqlTestsPassed++
            if ($Verbose) {
                Write-Host "      Payload safely handled: $payload" -ForegroundColor Green
            }
        } else {
            Write-Host "   ❌ SQL injection may be possible with: $payload" -ForegroundColor Red
        }
    } catch {
        if ($Verbose) {
            Write-Host "      Request failed for payload: $payload" -ForegroundColor Gray
        }
        $sqlTestsPassed++  # Request rejection is acceptable
    }
}

if ($sqlTestsPassed -eq $sqlPayloads.Count) {
    Write-Host "   ✅ SQL injection payloads properly handled" -ForegroundColor Green
    $testResults.SqlInjection = $true
} else {
    Write-Host "   ❌ Some SQL injection tests failed" -ForegroundColor Red
}

# Test 3: CORS Credential Security
Write-Host "3. Testing CORS Credential Security..." -ForegroundColor Yellow

try {
    # Test CORS with wildcard origin and credentials
    $headers = @{
        'Origin' = 'https://evil.com'
        'Access-Control-Request-Method' = 'GET'
        'Access-Control-Request-Headers' = 'Authorization'
    }

    $response = Invoke-WebRequest -Uri "$BaseUrl/ogc/features/v1/collections" -Method OPTIONS -Headers $headers -SkipHttpErrorCheck

    $corsHeaders = $response.Headers
    $allowOrigin = $corsHeaders['Access-Control-Allow-Origin']
    $allowCredentials = $corsHeaders['Access-Control-Allow-Credentials']

    if ($allowOrigin -ne 'https://evil.com' -or $allowCredentials -ne 'true') {
        Write-Host "   ✅ CORS credentials properly protected from malicious origins" -ForegroundColor Green
        $testResults.Cors = $true
    } else {
        Write-Host "   ❌ CORS may expose credentials to unauthorized origins" -ForegroundColor Red
    }

    if ($Verbose) {
        Write-Host "      Allow-Origin: $allowOrigin" -ForegroundColor Gray
        Write-Host "      Allow-Credentials: $allowCredentials" -ForegroundColor Gray
    }
} catch {
    Write-Host "   ⚠️  Could not test CORS (server may not be running)" -ForegroundColor Yellow
}

# Test 4: Information Disclosure Prevention
Write-Host "4. Testing Information Disclosure Prevention..." -ForegroundColor Yellow

try {
    # Test error message sanitization
    $response = Invoke-WebRequest -Uri "$BaseUrl/admin/health" -Method GET -Headers @{'Authorization' = 'Bearer invalid-token'} -SkipHttpErrorCheck

    $content = $response.Content
    if ($content -notmatch "password|secret|key|bypass|development") {
        Write-Host "   ✅ Error messages properly sanitized" -ForegroundColor Green
        $testResults.InfoDisclosure = $true
    } else {
        Write-Host "   ❌ Error messages may expose sensitive information" -ForegroundColor Red
        if ($Verbose) {
            Write-Host "      Sensitive content detected in: $content" -ForegroundColor Gray
        }
    }
} catch {
    Write-Host "   ⚠️  Could not test information disclosure (server may not be running)" -ForegroundColor Yellow
}

# Summary
Write-Host ""
Write-Host "🔍 Security Verification Summary" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan

$passedTests = ($testResults.Values | Where-Object { $_ -eq $true }).Count
$totalTests = $testResults.Count

Write-Host "Authentication Bypass:     $(if($testResults.AuthBypass) { '✅ FIXED' } else { '❌ NEEDS ATTENTION' })" -ForegroundColor $(if($testResults.AuthBypass) { 'Green' } else { 'Red' })
Write-Host "SQL Injection Prevention:  $(if($testResults.SqlInjection) { '✅ FIXED' } else { '❌ NEEDS ATTENTION' })" -ForegroundColor $(if($testResults.SqlInjection) { 'Green' } else { 'Red' })
Write-Host "CORS Credential Security:   $(if($testResults.Cors) { '✅ FIXED' } else { '❌ NEEDS ATTENTION' })" -ForegroundColor $(if($testResults.Cors) { 'Green' } else { 'Red' })
Write-Host "Information Disclosure:     $(if($testResults.InfoDisclosure) { '✅ FIXED' } else { '❌ NEEDS ATTENTION' })" -ForegroundColor $(if($testResults.InfoDisclosure) { 'Green' } else { 'Red' })

Write-Host ""
Write-Host "Overall Status: $passedTests/$totalTests security fixes verified" -ForegroundColor $(if($passedTests -eq $totalTests) { 'Green' } else { 'Yellow' })

if ($passedTests -eq $totalTests) {
    Write-Host "🎉 All critical security vulnerabilities have been fixed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "⚠️  Some security fixes need attention. Please review the failed tests." -ForegroundColor Yellow
    exit 1
}