param(
    [int]$GpuIndex = 0,
    [int]$TargetTempC = 70,      # drop clocks when temp goes above this
    [int]$CoolTempC = 65,        # raise clocks again once at/below this
    [int]$CriticalTempC = 76,    # big emergency clock drop above this
    [int]$CheckIntervalSec = 5,
    [int]$ClockCeilingMHz = 2100, # never lock the GPU above this (full-speed cap)
    [int]$ClockFloorMHz = 900,    # never lock below this (preserves some hashrate)
    [int]$ClockLockMinMHz = 180,  # lower bound of the lock range (lets GPU idle down)
    [int]$StepDownMHz = 75,
    [int]$StepUpMHz = 45,
    [int]$PowerLimitW = 0,        # 0 = leave power limit untouched; >0 = pin as a safety cap
    [switch]$NoRestoreOnExit
)

# WHY CLOCK LOCKING, NOT POWER LIMITING:
# The RTX PRO 4500 Blackwell has a power-limit range of only 150-200 W, so the
# old power-limit guard could barely move the temperature (200->180 W is ~10%).
# Its GPU-clock range is 180-3090 MHz. Capping the clock is the effective lever:
# measured 2160 MHz -> 84 C, 1500 MHz -> 68 C. This guard modulates the clock cap.

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-NvidiaSmiPath {
    $cmd = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
    if (Test-Path -LiteralPath $default) { return $default }
    throw 'nvidia-smi.exe was not found. Install NVIDIA driver tools or add nvidia-smi to PATH.'
}

function Invoke-NvidiaSmi {
    param([string[]]$Arguments)
    $output = & $script:NvidiaSmi @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "nvidia-smi failed with exit code ${exitCode}: $output"
    }
    return $output
}

function Get-GpuState {
    $query = 'index,name,temperature.gpu,clocks.sm,power.draw,fan.speed,power.min_limit,power.max_limit,power.default_limit'
    $line = Invoke-NvidiaSmi @('--id', "$GpuIndex", "--query-gpu=$query", '--format=csv,noheader,nounits')
    $parts = @($line -split ',\s*')
    if ($parts.Count -lt 6) { throw "Unexpected nvidia-smi output: $line" }
    return [pscustomobject]@{
        Index = [int]$parts[0]
        Name = $parts[1]
        TempC = [int][double]$parts[2]
        ClockSmMHz = [int][double]$parts[3]
        PowerDrawW = [double]$parts[4]
        FanPct = $parts[5]
        MinLimitW = [int][math]::Round([double]$parts[6])
        MaxLimitW = [int][math]::Round([double]$parts[7])
        DefaultLimitW = [int][math]::Round([double]$parts[8])
    }
}

function Set-ClockCeiling {
    param([int]$MaxMHz)
    Invoke-NvidiaSmi @('--id', "$GpuIndex", '--lock-gpu-clocks', "$ClockLockMinMHz,$MaxMHz") | Out-Null
}

function Reset-Clocks {
    Invoke-NvidiaSmi @('--id', "$GpuIndex", '--reset-gpu-clocks') | Out-Null
}

function Set-PowerLimit {
    param([int]$Watts)
    Invoke-NvidiaSmi @('--id', "$GpuIndex", '--power-limit', "$Watts") | Out-Null
}

if ($TargetTempC -le $CoolTempC) { throw 'TargetTempC must be higher than CoolTempC.' }
if ($ClockFloorMHz -gt $ClockCeilingMHz) { throw "ClockFloorMHz ($ClockFloorMHz) is higher than ClockCeilingMHz ($ClockCeilingMHz)." }

if (-not (Test-IsAdmin)) {
    Write-Host 'GPU clock/power control requires Administrator permission. Requesting elevation...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch] -or $value -is [bool]) {
            if ($value) { $argList += "-$key" }
        }
        else {
            $argList += "-$key"
            $argList += "$value"
        }
    }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    exit
}

$script:NvidiaSmi = Get-NvidiaSmiPath
$initial = Get-GpuState
$restorePowerW = $initial.DefaultLimitW

# Optional one-time power-limit safety cap.
if ($PowerLimitW -gt 0) {
    $pw = [Math]::Min([Math]::Max($PowerLimitW, $initial.MinLimitW), $initial.MaxLimitW)
    Set-PowerLimit -Watts $pw
    Write-Host "Power limit pinned to ${pw} W (safety cap)."
}

# Start at the ceiling and let the loop settle it down.
$currentCap = $ClockCeilingMHz
Set-ClockCeiling -MaxMHz $currentCap

Write-Host "GPU temperature guard (clock-lock) started at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')."
Write-Host "GPU:      #$($initial.Index) $($initial.Name)"
Write-Host "Target:   <= ${TargetTempC} C, recover at/below ${CoolTempC} C, critical at ${CriticalTempC} C"
Write-Host "Clock:    cap ${currentCap} MHz, floor ${ClockFloorMHz} MHz, ceiling ${ClockCeilingMHz} MHz (lock range ${ClockLockMinMHz}-cap)"
Write-Host 'Press Ctrl+C to stop.'
Write-Host ''

try {
    while ($true) {
        $state = Get-GpuState
        $desiredCap = $currentCap
        $action = 'hold'

        if ($state.TempC -ge $CriticalTempC) {
            $desiredCap = [Math]::Max($ClockFloorMHz, $currentCap - ($StepDownMHz * 3))
            $action = 'critical-drop'
        }
        elseif ($state.TempC -gt $TargetTempC) {
            $desiredCap = [Math]::Max($ClockFloorMHz, $currentCap - $StepDownMHz)
            $action = 'drop'
        }
        elseif ($state.TempC -le $CoolTempC -and $currentCap -lt $ClockCeilingMHz) {
            $desiredCap = [Math]::Min($ClockCeilingMHz, $currentCap + $StepUpMHz)
            $action = 'raise'
        }

        if ($desiredCap -ne $currentCap) {
            Set-ClockCeiling -MaxMHz $desiredCap
            $currentCap = $desiredCap
        }

        $stamp = Get-Date -Format 'HH:mm:ss'
        Write-Host ("{0} temp={1,2}C sm={2,4}MHz draw={3,6:N1}W fan={4,3} cap={5,4}MHz action={6}" -f `
            $stamp, $state.TempC, $state.ClockSmMHz, $state.PowerDrawW, $state.FanPct, $currentCap, $action)
        Start-Sleep -Seconds $CheckIntervalSec
    }
}
finally {
    if (-not $NoRestoreOnExit) {
        try {
            Reset-Clocks
            if ($PowerLimitW -gt 0) { Set-PowerLimit -Watts $restorePowerW }
            Write-Host "Reset GPU clocks$(if ($PowerLimitW -gt 0) { " and restored power limit to ${restorePowerW} W" })."
        }
        catch {
            Write-Host "Could not fully restore GPU settings: $($_.Exception.Message)"
        }
    }
}
