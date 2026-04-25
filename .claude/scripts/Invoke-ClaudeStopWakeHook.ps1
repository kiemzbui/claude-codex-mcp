param(
    [string]$WakeSignalsRoot = (Join-Path $env:USERPROFILE ".codex-manager\wake-signals"),
    [int]$WatchTimeoutMs = 1800000,
    [string]$CurrentSessionIdPath = (Join-Path $env:USERPROFILE ".codex-manager\current-session-id.txt")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-WakeSessionId {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim()
    if ($trimmed.IndexOfAny([char[]]@('\', '/')) -ge 0 -or
        $trimmed.EndsWith(".jsonl", [System.StringComparison]::OrdinalIgnoreCase)) {
        $leafName = [System.IO.Path]::GetFileNameWithoutExtension($trimmed)
        if (-not [string]::IsNullOrWhiteSpace($leafName)) {
            return $leafName
        }
    }

    return $trimmed
}

function Get-WakeSessionId {
    param(
        [AllowNull()]
        [object]$HookInput
    )

    if ($null -eq $HookInput) {
        return $null
    }

    $candidates = [System.Collections.Generic.List[string]]::new()

    if ($HookInput.PSObject.Properties.Match("session_id").Count -gt 0) {
        $candidates.Add([string]$HookInput.session_id)
    }

    if ($HookInput.PSObject.Properties.Match("transcript_path").Count -gt 0) {
        $candidates.Add([string]$HookInput.transcript_path)
    }

    foreach ($candidate in $candidates) {
        $normalized = ConvertTo-WakeSessionId -Value $candidate
        if (-not [string]::IsNullOrWhiteSpace($normalized)) {
            return $normalized
        }
    }

    return $null
}

function Get-StopHookActive {
    param(
        [AllowNull()]
        [object]$HookInput
    )

    if ($null -eq $HookInput -or $HookInput.PSObject.Properties.Match("stop_hook_active").Count -eq 0) {
        return $false
    }

    $value = $HookInput.stop_hook_active
    if ($value -is [bool]) {
        return $value
    }

    return [string]$value -eq "true"
}

function Update-CurrentSessionId {
    param(
        [string]$SessionId,
        [string]$TargetPath
    )

    try {
        $parent = Split-Path -Parent $TargetPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        Set-Content -LiteralPath $TargetPath -Value $SessionId -NoNewline
    } catch {
        # Wake handling should continue even if the session-id cache write fails.
    }
}

function Get-NextSignalFile {
    param(
        [string]$SignalDirectory
    )

    if (-not (Test-Path -LiteralPath $SignalDirectory -PathType Container)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $SignalDirectory -Filter "*.json" -File |
        Sort-Object LastWriteTimeUtc, Name |
        Select-Object -First 1
}

function Format-MessagePart {
    param(
        [AllowNull()]
        [string]$Value,
        [int]$MaxLength
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $collapsed = [System.Text.RegularExpressions.Regex]::Replace($Value.Trim(), "\s+", " ")
    if ($collapsed.Length -le $MaxLength) {
        return $collapsed
    }

    return $collapsed.Substring(0, $MaxLength - 1) + "..."
}

function Get-WakeMessage {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Signal
    )

    $title = ""
    if ($Signal.PSObject.Properties.Match("title").Count -gt 0) {
        $title = [string]$Signal.title
    }

    if ([string]::IsNullOrWhiteSpace($title) -and $Signal.PSObject.Properties.Match("jobId").Count -gt 0) {
        $title = [string]$Signal.jobId
    }

    $status = ""
    if ($Signal.PSObject.Properties.Match("status").Count -gt 0) {
        $status = [string]$Signal.status
    }

    $summary = ""
    if ($Signal.PSObject.Properties.Match("resultSummary").Count -gt 0) {
        $summary = [string]$Signal.resultSummary
    }

    $title = Format-MessagePart -Value $title -MaxLength 120
    $status = Format-MessagePart -Value $status -MaxLength 40
    $summary = Format-MessagePart -Value $summary -MaxLength 240

    if ([string]::IsNullOrWhiteSpace($title)) {
        $title = "unknown job"
    }

    if ([string]::IsNullOrWhiteSpace($status)) {
        $status = "finished"
    }

    $message = "Codex job '$title' finished: $status."
    if (-not [string]::IsNullOrWhiteSpace($summary)) {
        $message += " $summary"
    }

    return "$message Use codex_status/codex_result for details."
}

function TryConsumeSignal {
    param(
        [string]$SignalPath
    )

    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            $raw = [System.IO.File]::ReadAllText($SignalPath)
            if ([string]::IsNullOrWhiteSpace($raw)) {
                throw "Wake signal '$SignalPath' was empty."
            }

            $signal = $raw | ConvertFrom-Json
            Remove-Item -LiteralPath $SignalPath -Force -ErrorAction Stop
            [Console]::Error.WriteLine((Get-WakeMessage -Signal $signal))
            return $true
        } catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 100
        } catch [System.UnauthorizedAccessException] {
            Start-Sleep -Milliseconds 100
        } catch {
            if ($attempt -ge 9) {
                [Console]::Error.WriteLine("Failed to consume Codex wake signal '$SignalPath': $($_.Exception.Message)")
                return $false
            }

            Start-Sleep -Milliseconds 100
        }
    }

    return $false
}

$stdin = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($stdin)) {
    exit 0
}

try {
    $hookInput = $stdin | ConvertFrom-Json
} catch {
    exit 0
}

if (Get-StopHookActive -HookInput $hookInput) {
    exit 0
}

$wakeSessionId = Get-WakeSessionId -HookInput $hookInput
if ([string]::IsNullOrWhiteSpace($wakeSessionId)) {
    exit 0
}

Update-CurrentSessionId -SessionId $wakeSessionId -TargetPath $CurrentSessionIdPath

if ([string]::IsNullOrWhiteSpace($WakeSignalsRoot)) {
    exit 0
}

$signalDirectory = Join-Path $WakeSignalsRoot $wakeSessionId
New-Item -ItemType Directory -Path $signalDirectory -Force | Out-Null

$existingSignal = Get-NextSignalFile -SignalDirectory $signalDirectory
if ($null -ne $existingSignal -and (TryConsumeSignal -SignalPath $existingSignal.FullName)) {
    exit 2
}

$deadline = [System.DateTimeOffset]::UtcNow.AddMilliseconds($WatchTimeoutMs)
$watcher = [System.IO.FileSystemWatcher]::new($signalDirectory, "*.json")
$watcher.IncludeSubdirectories = $false
$watcher.NotifyFilter = [System.IO.NotifyFilters]::FileName

try {
    while ([System.DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(
            1,
            [int][Math]::Ceiling(($deadline - [System.DateTimeOffset]::UtcNow).TotalMilliseconds))
        $null = $watcher.WaitForChanged(
            [System.IO.WatcherChangeTypes]::Created -bor [System.IO.WatcherChangeTypes]::Renamed,
            $remaining)

        $nextSignal = Get-NextSignalFile -SignalDirectory $signalDirectory
        if ($null -ne $nextSignal -and (TryConsumeSignal -SignalPath $nextSignal.FullName)) {
            exit 2
        }
    }
} finally {
    $watcher.Dispose()
}

exit 0
