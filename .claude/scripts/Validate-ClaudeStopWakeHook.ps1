Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$hookScript = Join-Path $PSScriptRoot "Invoke-ClaudeStopWakeHook.ps1"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("claude-stop-hook-validation-" + [guid]::NewGuid().ToString("n"))
$completed = [System.Collections.Generic.List[string]]::new()

function Invoke-Hook {
    param(
        [Parameter(Mandatory = $true)]
        [string]$HookInputJson,
        [Parameter(Mandatory = $true)]
        [int]$WatchTimeoutMs,
        [Parameter(Mandatory = $true)]
        [string]$WakeSignalsRoot
    )

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = "powershell.exe"
    $process.StartInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$hookScript`" -WatchTimeoutMs $WatchTimeoutMs -WakeSignalsRoot `"$WakeSignalsRoot`""
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true

    $null = $process.Start()
    $process.StandardInput.Write($HookInputJson)
    $process.StandardInput.Close()

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    $combinedOutput = @($stdout.Trim(), $stderr.Trim()) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { "$_" }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output   = ($combinedOutput -join "`n").Trim()
    }
}

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $sessionIdPrimary = "session-primary"
    $primaryDir = Join-Path $tempRoot $sessionIdPrimary
    New-Item -ItemType Directory -Path $primaryDir -Force | Out-Null
    @{
        wakeSessionId = $sessionIdPrimary
        jobId = "job-primary"
        title = "Primary Session"
        status = "Completed"
        resultSummary = "existing signal path"
        ts = "2026-04-24T00:00:00Z"
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $primaryDir "job-primary.json") -Encoding UTF8

    $primaryInput = @{
        session_id = $sessionIdPrimary
        transcript_path = "C:\transcripts\$sessionIdPrimary.jsonl"
        hook_event_name = "Stop"
        stop_hook_active = $false
    } | ConvertTo-Json -Compress
    $primaryResult = Invoke-Hook -HookInputJson $primaryInput -WatchTimeoutMs 250 -WakeSignalsRoot $tempRoot
    Assert-Condition ($primaryResult.ExitCode -eq 2) "Primary session_id case should rewake with exit code 2."
    Assert-Condition ($primaryResult.Output -match "Primary Session") "Primary session_id case should mention the job title."
    $completed.Add("existing-signal session_id")

    $sessionIdFallback = "session-fallback"
    $fallbackDir = Join-Path $tempRoot $sessionIdFallback
    New-Item -ItemType Directory -Path $fallbackDir -Force | Out-Null
    @{
        wakeSessionId = $sessionIdFallback
        jobId = "job-fallback"
        title = "Transcript Fallback"
        status = "Failed"
        resultSummary = "transcript basename fallback"
        ts = "2026-04-24T00:00:00Z"
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $fallbackDir "job-fallback.json") -Encoding UTF8

    $fallbackInput = @{
        transcript_path = "C:\transcripts\$sessionIdFallback.jsonl"
        hook_event_name = "Stop"
        stop_hook_active = $false
    } | ConvertTo-Json -Compress
    $fallbackResult = Invoke-Hook -HookInputJson $fallbackInput -WatchTimeoutMs 250 -WakeSignalsRoot $tempRoot
    Assert-Condition ($fallbackResult.ExitCode -eq 2) "Transcript fallback case should rewake with exit code 2."
    Assert-Condition ($fallbackResult.Output -match "Transcript Fallback") "Transcript fallback case should mention the fallback job title."
    $completed.Add("existing-signal transcript fallback")

    $sessionIdWatch = "session-watch"
    $watchDir = Join-Path $tempRoot $sessionIdWatch
    $watchJob = Start-Job -ScriptBlock {
        param($DirectoryPath)
        Start-Sleep -Milliseconds 300
        New-Item -ItemType Directory -Path $DirectoryPath -Force | Out-Null
        @{
            wakeSessionId = [System.IO.Path]::GetFileName($DirectoryPath)
            jobId = "job-watch"
            title = "Watched Signal"
            status = "Completed"
            resultSummary = "watch path caught a late signal"
            ts = "2026-04-24T00:00:00Z"
        } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $DirectoryPath "job-watch.json") -Encoding UTF8
    } -ArgumentList $watchDir

    try {
        $watchInput = @{
            session_id = $sessionIdWatch
            hook_event_name = "Stop"
            stop_hook_active = $false
        } | ConvertTo-Json -Compress
        $watchResult = Invoke-Hook -HookInputJson $watchInput -WatchTimeoutMs 3000 -WakeSignalsRoot $tempRoot
        Assert-Condition ($watchResult.ExitCode -eq 2) "Watcher case should rewake when a signal arrives later."
        Assert-Condition ($watchResult.Output -match "Watched Signal") "Watcher case should mention the watched job title."
    } finally {
        Wait-Job -Job $watchJob | Out-Null
        Receive-Job -Job $watchJob | Out-Null
        Remove-Job -Job $watchJob -Force
    }
    $completed.Add("watch mode late signal")

    $timeoutInput = @{
        session_id = "session-timeout"
        hook_event_name = "Stop"
        stop_hook_active = $false
    } | ConvertTo-Json -Compress
    $timeoutResult = Invoke-Hook -HookInputJson $timeoutInput -WatchTimeoutMs 200 -WakeSignalsRoot $tempRoot
    Assert-Condition ($timeoutResult.ExitCode -eq 0) "Timeout case should exit 0 when no signal arrives."
    Assert-Condition ([string]::IsNullOrWhiteSpace($timeoutResult.Output)) "Timeout case should not emit a rewake message."
    $completed.Add("timeout without signal")

    $guardSession = "session-guard"
    $guardDir = Join-Path $tempRoot $guardSession
    New-Item -ItemType Directory -Path $guardDir -Force | Out-Null
    @{
        wakeSessionId = $guardSession
        jobId = "job-guard"
        title = "Guarded Session"
        status = "Completed"
        resultSummary = "stop hook active should not loop"
        ts = "2026-04-24T00:00:00Z"
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $guardDir "job-guard.json") -Encoding UTF8

    $guardInput = @{
        session_id = $guardSession
        hook_event_name = "Stop"
        stop_hook_active = $true
    } | ConvertTo-Json -Compress
    $guardResult = Invoke-Hook -HookInputJson $guardInput -WatchTimeoutMs 200 -WakeSignalsRoot $tempRoot
    Assert-Condition ($guardResult.ExitCode -eq 0) "stop_hook_active case should not block stopping again."
    Assert-Condition (Test-Path -LiteralPath (Join-Path $guardDir "job-guard.json")) "stop_hook_active case should leave signals untouched."
    $completed.Add("stop_hook_active guard")

    Write-Output ("Validated stop hook helper cases: " + ($completed -join ", "))
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
