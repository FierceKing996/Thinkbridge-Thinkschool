<#
.SYNOPSIS
    Day 21 load test: proves HybridCache's stampede protection on
    GET /api/reports/authors and reports the DB-load drop it buys.

.DESCRIPTION
    Fires N concurrent requests at the authors report endpoint in two bursts:

      Burst 1 (cold cache)  - resets the DB-query counter first, so every
                               concurrent request races the same cache miss.
                               Without stampede protection this would produce
                               N DB queries; HybridCache's GetOrCreateAsync
                               coalesces concurrent misses for the same key
                               into a single in-flight call, so it should
                               produce exactly 1.

      Burst 2 (warm cache)  - runs immediately after, while the entry is
                               still fresh. Every request should be a cache
                               hit, so the DB-query counter shouldn't move at
                               all: 0 additional queries for N requests.

    Uses a PowerShell 5.1-compatible runspace pool for true concurrency (no
    dependency on ForEach-Object -Parallel, which needs PS7+).

.PARAMETER BaseUrl
    Base URL of a running QuotesApi instance (default: http://localhost:5062 -
    check your launchSettings.json / `dotnet run` output and override with
    -BaseUrl if it differs).

.PARAMETER Concurrency
    Number of concurrent requests per burst (default: 50).
#>
param(
    [string]$BaseUrl = "http://localhost:5062",
    [int]$Concurrency = 50
)

$ErrorActionPreference = "Stop"

function Get-Stats {
    (Invoke-RestMethod -Uri "$BaseUrl/api/reports/authors/stats").dbQueryCount
}

function Invoke-Burst {
    param([string]$Url, [int]$Count)

    $pool = [runspacefactory]::CreateRunspacePool(1, [Math]::Max($Count, 1))
    $pool.Open()
    $jobs = @()

    for ($i = 0; $i -lt $Count; $i++) {
        $ps = [powershell]::Create()
        $ps.RunspacePool = $pool
        [void]$ps.AddScript({
            param($u)
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                Invoke-WebRequest -Uri $u -UseBasicParsing | Out-Null
                $ok = $true
            } catch {
                $ok = $false
            }
            $sw.Stop()
            [pscustomobject]@{ Success = $ok; Ms = $sw.Elapsed.TotalMilliseconds }
        }).AddArgument($Url)
        $jobs += [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
    }

    $results = foreach ($job in $jobs) {
        $job.PS.EndInvoke($job.Handle)
        $job.PS.Dispose()
    }

    $pool.Close()
    $pool.Dispose()
    return $results
}

function Show-BurstSummary {
    param([string]$Label, $Results, [int]$DbQueriesBefore, [int]$DbQueriesAfter)

    $latencies = $Results | Sort-Object Ms
    $p50 = $latencies[[int]([Math]::Floor($latencies.Count * 0.50))].Ms
    $p99 = $latencies[[int]([Math]::Min($latencies.Count - 1, [Math]::Floor($latencies.Count * 0.99)))].Ms
    $failures = ($Results | Where-Object { -not $_.Success }).Count
    $dbQueries = $DbQueriesAfter - $DbQueriesBefore

    Write-Host ""
    Write-Host "== $Label ==" -ForegroundColor Cyan
    Write-Host ("Requests:        {0}" -f $Results.Count)
    Write-Host ("Failures:        {0}" -f $failures)
    Write-Host ("p50 latency:     {0:N1} ms" -f $p50)
    Write-Host ("p99 latency:     {0:N1} ms" -f $p99)
    Write-Host ("DB queries hit:  {0}" -f $dbQueries)
    Write-Host ("Cache hit rate:  {0:P1}" -f (1 - ($dbQueries / [double]$Results.Count)))
}

Write-Host "Target: $BaseUrl  Concurrency: $Concurrency"

Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/reports/authors/stats/reset" | Out-Null

$before1 = Get-Stats
$burst1 = Invoke-Burst -Url "$BaseUrl/api/reports/authors" -Count $Concurrency
$after1 = Get-Stats
Show-BurstSummary -Label "Burst 1: cold cache (stampede protection)" -Results $burst1 -DbQueriesBefore $before1 -DbQueriesAfter $after1

$before2 = Get-Stats
$burst2 = Invoke-Burst -Url "$BaseUrl/api/reports/authors" -Count $Concurrency
$after2 = Get-Stats
Show-BurstSummary -Label "Burst 2: warm cache" -Results $burst2 -DbQueriesBefore $before2 -DbQueriesAfter $after2

Write-Host ""
if ($after1 - $before1 -le 1) {
    Write-Host "Stampede protection confirmed: $Concurrency concurrent cold requests produced only $($after1 - $before1) DB query." -ForegroundColor Green
} else {
    Write-Host "Expected 1 DB query for the cold burst but saw $($after1 - $before1) - check caching wiring." -ForegroundColor Yellow
}
