<#
.SYNOPSIS
    Collects machine hardware and environment information.

.DESCRIPTION
    Gathers CPU, memory, OS, and runtime details for the current machine.
    This metadata is included in result files so that performance numbers
    can be properly contextualized and compared across different environments.

.EXAMPLE
    $info = .\harness\Get-MachineInfo.ps1
    $info.Cpu.Name   # e.g. "Intel Core i7-12700H"
    $info.Memory.TotalGB  # e.g. 32
#>
[CmdletBinding()]
param()

# ── CPU ─────────────────────────────────────────────────────────────────────

$cpuInfo = [ordered]@{
    Name             = $null
    PhysicalCores    = $null
    LogicalProcessors = [Environment]::ProcessorCount
    MaxClockSpeedMHz = $null
    Architecture     = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
}

try {
    if ($IsWindows -or (-not (Test-Path variable:IsWindows))) {
        $wmiCpu = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
        $cpuInfo.Name             = $wmiCpu.Name.Trim()
        $cpuInfo.PhysicalCores    = $wmiCpu.NumberOfCores
        $cpuInfo.MaxClockSpeedMHz = $wmiCpu.MaxClockSpeed
    }
    elseif ($IsLinux) {
        $lscpu = lscpu 2>$null
        if ($lscpu) {
            $cpuInfo.Name          = ($lscpu | Select-String '^Model name:' | ForEach-Object { ($_ -split ':\s+', 2)[1] })
            $cpuInfo.PhysicalCores = [int]($lscpu | Select-String '^Core\(s\) per socket:' | ForEach-Object { ($_ -split ':\s+', 2)[1] })
        }
    }
    elseif ($IsMacOS) {
        $cpuInfo.Name          = (sysctl -n machdep.cpu.brand_string 2>$null)
        $cpuInfo.PhysicalCores = [int](sysctl -n hw.physicalcpu 2>$null)
    }
}
catch {
    Write-Verbose "Could not retrieve detailed CPU info: $_"
}

# ── Memory ──────────────────────────────────────────────────────────────────

$memoryInfo = [ordered]@{
    TotalGB     = $null
    AvailableGB = $null
}

try {
    if ($IsWindows -or (-not (Test-Path variable:IsWindows))) {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $memoryInfo.TotalGB     = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        $memoryInfo.AvailableGB = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
    }
    elseif ($IsLinux) {
        $memKB = (Get-Content /proc/meminfo -ErrorAction Stop | Select-String '^MemTotal:' | ForEach-Object { ($_ -split '\s+')[1] })
        if ($memKB) { $memoryInfo.TotalGB = [math]::Round([long]$memKB / 1MB, 1) }
        $availKB = (Get-Content /proc/meminfo -ErrorAction Stop | Select-String '^MemAvailable:' | ForEach-Object { ($_ -split '\s+')[1] })
        if ($availKB) { $memoryInfo.AvailableGB = [math]::Round([long]$availKB / 1MB, 1) }
    }
    elseif ($IsMacOS) {
        $totalBytes = [long](sysctl -n hw.memsize 2>$null)
        if ($totalBytes) { $memoryInfo.TotalGB = [math]::Round($totalBytes / 1GB, 1) }
    }
}
catch {
    Write-Verbose "Could not retrieve memory info: $_"
}

# ── Operating System ────────────────────────────────────────────────────────

$osInfo = [ordered]@{
    Description = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    Platform    = if ($IsWindows -or (-not (Test-Path variable:IsWindows))) { 'Windows' }
                  elseif ($IsLinux) { 'Linux' }
                  elseif ($IsMacOS) { 'macOS' }
                  else { 'Unknown' }
    Version     = [Environment]::OSVersion.VersionString
}

# ── Runtime ─────────────────────────────────────────────────────────────────

$dotnetVersion = $null
try {
    $dotnetVersion = (dotnet --version 2>$null)
}
catch {
    Write-Verbose "Could not retrieve .NET SDK version: $_"
}

$runtimeInfo = [ordered]@{
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    DotnetSdkVersion  = $dotnetVersion
    ClrVersion         = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
}

# ── Assemble ────────────────────────────────────────────────────────────────

$machineInfo = [PSCustomObject][ordered]@{
    MachineName = [Environment]::MachineName
    Cpu         = [PSCustomObject]$cpuInfo
    Memory      = [PSCustomObject]$memoryInfo
    OS          = [PSCustomObject]$osInfo
    Runtime     = [PSCustomObject]$runtimeInfo
    CollectedAt = (Get-Date -Format 'o')
}

return $machineInfo
