<#
  Startup / memory / CPU of the client build on one Windows machine.
  Startup: process start -> main window handle exists and the UI thread is input-idle.
  GUI tree and omp tree (bun.exe / omp.exe descendants) are measured separately.
  Not executed by the author (Linux container); run on the real machine, with no other heavy load:

    powershell -ExecutionPolicy Bypass -File tools\verify\windows-benchmark.ps1 `
      -Exe "C:\path\to\omp-gui-<version>-win-x64\OmpGui.exe" -Runs 5 -Out bench.csv

  Close any running copy of the client first. The client starts omp itself; idle memory is sampled after
  -SettleSeconds. For the streaming scenario, start a prompt by hand when asked and press Enter here; the script then
  samples for -StreamSeconds. -Label names the build in the CSV, so runs of two builds can be compared.
#>
param(
  [Parameter(Mandatory)] [string]$Exe,
  [string]$Label = 'client',
  [int]$Runs = 5,
  [int]$SettleSeconds = 30,
  [int]$StreamSeconds = 30,
  [string]$Out = 'bench.csv',
  [switch]$SkipStreaming
)
$ErrorActionPreference = 'Stop'
$rows = New-Object System.Collections.Generic.List[object]

function Get-Tree([int]$rootPid) {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
  $ids = New-Object System.Collections.Generic.List[object]
  $queue = New-Object System.Collections.Generic.Queue[int]; $queue.Enqueue($rootPid)
  while ($queue.Count) {
    $p = $queue.Dequeue(); $proc = $all | Where-Object ProcessId -eq $p
    if ($proc) { $ids.Add($proc) }
    $all | Where-Object ParentProcessId -eq $p | ForEach-Object { $queue.Enqueue([int]$_.ProcessId) }
  }
  $ids
}

function Split-Tree([int]$rootPid) {
  # omp = bun.exe / omp.exe and everything below them; GUI = the rest
  $tree = Get-Tree $rootPid
  $ompRoots = $tree | Where-Object { $_.Name -match '^(bun|omp)\.exe$' -and ($tree.ProcessId -contains $_.ParentProcessId) -and (($tree | Where-Object ProcessId -eq $_.ParentProcessId).Name -notmatch '^(bun|omp)\.exe$') }
  $omp = @(); foreach ($r in $ompRoots) { $omp += Get-Tree ([int]$r.ProcessId) }
  $gui = $tree | Where-Object { $omp.ProcessId -notcontains $_.ProcessId }
  @{ Gui = @($gui); Omp = @($omp) }
}

function Measure-Set($procs) {
  $ws = 0; $priv = 0; $cpu = 0.0; $n = 0
  foreach ($p in $procs) {
    try { $gp = Get-Process -Id $p.ProcessId -ErrorAction Stop; $ws += $gp.WorkingSet64; $priv += $gp.PrivateMemorySize64; $cpu += $gp.TotalProcessorTime.TotalMilliseconds; $n++ } catch { }
  }
  @{ WsMiB = [math]::Round($ws / 1MB, 1); PrivMiB = [math]::Round($priv / 1MB, 1); CpuMs = $cpu; Count = $n }
}

function Cpu-Percent([int]$rootPid, [int]$seconds, [string]$part) {
  $a = Measure-Set ((Split-Tree $rootPid).$part); Start-Sleep -Seconds $seconds
  $b = Measure-Set ((Split-Tree $rootPid).$part)
  [math]::Round(($b.CpuMs - $a.CpuMs) / ($seconds * 1000) / [Environment]::ProcessorCount * 100, 2)
}

function Add-Row($metric, $value, $unit) { $rows.Add([pscustomobject]@{ gui = $Label; metric = $metric; value = $value; unit = $unit }) }

for ($i = 1; $i -le $Runs; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $Exe -PassThru
  while ($p.MainWindowHandle -eq 0 -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 10; $p.Refresh() }
  $null = $p.WaitForInputIdle(30000)
  $usable = $sw.Elapsed.TotalMilliseconds
  Add-Row "startup_run$i" ([math]::Round($usable)) 'ms'
  Start-Sleep -Seconds $SettleSeconds
  if ($i -eq 1) {
    $parts = Split-Tree $p.Id
    $g = Measure-Set $parts.Gui; $o = Measure-Set $parts.Omp
    Add-Row 'idle_gui_working_set' $g.WsMiB 'MiB'; Add-Row 'idle_gui_private' $g.PrivMiB 'MiB'
    Add-Row 'idle_gui_processes' $g.Count 'count'
    Add-Row 'idle_omp_working_set' $o.WsMiB 'MiB'; Add-Row 'idle_omp_processes' $o.Count 'count'
    Add-Row 'idle_gui_cpu' (Cpu-Percent $p.Id 30 'Gui') '% of machine'
    if (-not $SkipStreaming) {
      Read-Host "Start a long streaming prompt in the client, then press Enter"
      $parts = Split-Tree $p.Id; $g = Measure-Set $parts.Gui; $o = Measure-Set $parts.Omp
      Add-Row 'streaming_gui_working_set' $g.WsMiB 'MiB'; Add-Row 'streaming_omp_working_set' $o.WsMiB 'MiB'
      Add-Row 'streaming_gui_cpu' (Cpu-Percent $p.Id $StreamSeconds 'Gui') '% of machine'
      Read-Host "Minimize the client window (keep it streaming), then press Enter"
      Add-Row 'background_gui_cpu' (Cpu-Percent $p.Id $StreamSeconds 'Gui') '% of machine'
    }
  }
  Get-Tree $p.Id | Sort-Object ProcessId -Descending | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 3
}
$dir = Split-Path -Parent $Exe
Add-Row 'gui_artifact_size' ([math]::Round(((Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)) 'MiB'
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 $Out
$rows | Format-Table -AutoSize
