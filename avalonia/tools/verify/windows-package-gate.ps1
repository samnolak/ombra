<#
  Windows gate for a release candidate package (win-x64 / win-arm64 zip). Run on a real Windows machine; it needs no
  admin rights and does not touch the user's profile, settings, omp or PATH: every run uses a throwaway profile folder.
  Prepared in a Linux container; NOT EXECUTED by its author (no Windows machine) — the result file it writes is the
  evidence.

    powershell -ExecutionPolicy Bypass -File tools\verify\windows-package-gate.ps1 -Zip omp-gui-<ver>-win-x64.zip [-Source <repo>\avalonia]

  Checks:
    1. The zip matches its .sha256 (next to it) and, when SHA256SUMS is next to it, that list too.
    2. Signature state is what <package>.signing.txt says (Authenticode "Valid" only when it says signed).
    3. OmpGui.exe --self-test --install-runtime with an empty profile: Skia, HarfBuzz, ConPTY shell, speech engine,
       the pinned runtime installed by the client, omp over RPC, nothing left running.
    4. The same with SHELL=/usr/bin/bash (as a Git Bash parent sets it; not a Windows path): the terminal must still
       start a shell (regression of 49077f6 — CI's Windows run found it; it was fixed and verified on Linux only).
    5. With -Source: the client test suite on this machine (dotnet 10 SDK needed), including SelfTestProcessTests.
    6. With -Source (node needed for the stand-in model server): the packaged app against the real omp it installed,
       driven through Win32 input: omp starts over RPC v2, a prompt streams, the window is minimized (Core keeps
       folding events, UI paused), the run ends while minimized (window title mark + taskbar flash = the Windows
       notification path), restore shows the current state, typing + Enter sends (keyboard focus in the composer),
       Esc stops a streaming reply, DPI of the window recorded with a screenshot, WM_CLOSE leaves nothing running.
    The last lines are MANUAL checks for a person at the machine (150 % scaling, toast-free attention, dialogs).
#>
param(
  [Parameter(Mandatory = $true)][string]$Zip,
  [string]$Source,
  [string]$Out = 'windows-gate-result.txt'
)
$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[string]
$fails = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
  $line = ('{0} {1} - {2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail)
  $results.Add($line); Write-Host $line
  if (-not $ok) { $script:fails++ }
}

$Zip = (Resolve-Path -LiteralPath $Zip).Path
$dir = Split-Path -Parent $Zip
$name = Split-Path -Leaf $Zip
$results.Add("windows package gate $(Get-Date -Format o) on $([Environment]::OSVersion.VersionString) $env:PROCESSOR_ARCHITECTURE")

# 1. Checksums
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash.ToLowerInvariant()
$shaFile = "$Zip.sha256"
if (Test-Path -LiteralPath $shaFile) {
  $expected = ((Get-Content -LiteralPath $shaFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
  Check 'sha256' ($actual -eq $expected) "$name $actual"
} else { Check 'sha256' $false "no $name.sha256 next to the zip" }
$sums = Join-Path $dir 'SHA256SUMS'
if (Test-Path -LiteralPath $sums) {
  $line = Get-Content -LiteralPath $sums | Where-Object { $_ -match [regex]::Escape($name) } | Select-Object -First 1
  Check 'SHA256SUMS' ($line -and $line.StartsWith($actual)) "$line"
}

# Unpacked into a fresh folder
$work = Join-Path ([IO.Path]::GetTempPath()) ('ompgui-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
Expand-Archive -LiteralPath $Zip -DestinationPath (Join-Path $work 'app')
$exe = Get-ChildItem -Path (Join-Path $work 'app') -Recurse -Filter OmpGui.exe | Select-Object -First 1
Check 'unpacked' ($null -ne $exe) "$($exe.FullName)"

# 2. Signature state
$record = Join-Path $dir ($name -replace '\.zip$', '.signing.txt')
$sig = Get-AuthenticodeSignature -LiteralPath $exe.FullName
if (Test-Path -LiteralPath $record) {
  $says = Get-Content -LiteralPath $record -Raw
  if ($says -match 'UNSIGNED') { Check 'signature' ($sig.Status -ne 'Valid') "record says unsigned; Authenticode status $($sig.Status)" }
  else { Check 'signature' ($sig.Status -eq 'Valid') "record: $($says.Trim()); Authenticode status $($sig.Status) by $($sig.SignerCertificate.Subject)" }
} else { Check 'signature' $false "no signing record $record" }

function SelfTest([string]$label, [hashtable]$extraEnv) {
  $profileDir = Join-Path $work "profile-$label"
  foreach ($d in 'Roaming', 'Local', 'Temp') { New-Item -ItemType Directory -Force -Path (Join-Path $profileDir $d) | Out-Null }
  $report = Join-Path $work "selftest-$label.json"
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $exe.FullName
  $psi.Arguments = "--self-test `"$report`" --install-runtime"
  $psi.UseShellExecute = $false
  $psi.EnvironmentVariables['USERPROFILE'] = $profileDir
  $psi.EnvironmentVariables['APPDATA'] = (Join-Path $profileDir 'Roaming')
  $psi.EnvironmentVariables['LOCALAPPDATA'] = (Join-Path $profileDir 'Local')
  $psi.EnvironmentVariables['TEMP'] = (Join-Path $profileDir 'Temp')
  $psi.EnvironmentVariables['TMP'] = (Join-Path $profileDir 'Temp')
  # .NET resolves %LOCALAPPDATA% / %APPDATA% through the shell, not these variables: the client's own overrides keep
  # its runtime and settings in the throwaway folder; omp (Bun) takes its home from USERPROFILE.
  $psi.EnvironmentVariables['OMPGUI_RUNTIME_DIR'] = (Join-Path $profileDir 'runtimes')
  $psi.EnvironmentVariables['OMPGUI_CONFIG'] = (Join-Path $profileDir 'omp-gui.local.json')
  foreach ($k in $extraEnv.Keys) { $psi.EnvironmentVariables[$k] = $extraEnv[$k] }
  $p = [System.Diagnostics.Process]::Start($psi)
  if (-not $p.WaitForExit(20 * 60 * 1000)) { $p.Kill(); Check "self-test-$label" $false 'timed out after 20 min'; return }
  if (-not (Test-Path -LiteralPath $report)) { Check "self-test-$label" $false "exit $($p.ExitCode), no report"; return }
  $r = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
  foreach ($c in $r.checks) { $results.Add(('  {0} {1} {2}: {3}' -f $label, $(if ($c.ok) { 'ok  ' } else { 'FAIL' }), $c.name, $c.detail)) }
  Check "self-test-$label" ([bool]$r.pass) "exit $($p.ExitCode); required checks $(if ($r.pass) { 'all passed' } else { 'failed' })"
  $pty = $r.checks | Where-Object { $_.name -eq 'pty' }
  Check "terminal-shell-$label" ([bool]$pty.ok) "$($pty.detail)"
  $left = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($work) }
  Check "nothing-left-running-$label" ($null -eq $left) "$(@($left).Count) process(es) from the unpacked app or its profile"
}

# 3. Clean profile
SelfTest 'clean' @{}
# 4. SHELL set the way a Git Bash parent sets it
SelfTest 'git-bash-shell' @{ 'SHELL' = '/usr/bin/bash' }

# 5. Source tests
if ($Source) {
  Push-Location $Source
  try {
    & dotnet test tests/OmpGui.Tests -c Release --logger "console;verbosity=normal" --logger "trx;LogFileName=windows-gate.trx" --results-directory (Join-Path $work 'trx') 2>&1 | Tee-Object -FilePath (Join-Path $work 'tests.log') | Select-Object -Last 3 | ForEach-Object { $results.Add("  $_") }
    Check 'client-tests' ($LASTEXITCODE -eq 0) "dotnet test exit $LASTEXITCODE (log and TRX in $work)"
  } finally { Pop-Location }
}

# 6. The packaged app at run time (stand-in model; omp, RPC, provider path and the app are real)
if ($Source) {
  Add-Type -Namespace Gate -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
  Add-Type -AssemblyName System.Windows.Forms, System.Drawing
  $rtProfile = Join-Path $work 'profile-clean'                  # the runtime the clean self-test installed
  $rt = Join-Path $rtProfile 'runtimes\omp-18.2.0-bun-1.4.2'
  $home2 = Join-Path $work 'profile-run'
  $agent = Join-Path $home2 '.omp\profiles\standin\agent'
  New-Item -ItemType Directory -Force -Path $agent, (Join-Path $home2 'Roaming'), (Join-Path $home2 'Local'), (Join-Path $work 'proj') | Out-Null
  $port = 18182
  Set-Content -LiteralPath (Join-Path $agent 'config.yml') -Value "modelRoles:`n  default: stand-in-mock/scripted-stand-in`n"
  Set-Content -LiteralPath (Join-Path $agent 'models.yml') -Value @"
providers:
  stand-in-mock:
    baseUrl: http://127.0.0.1:$port/v1
    api: openai-completions
    apiKey: LOCAL_MODEL_API_KEY
    models:
      - id: scripted-stand-in
        name: Stand-in (scripted mock server, not a model)
        reasoning: false
        input: [text]
        maxTokens: 8192
        contextWindow: 131072
"@
  $cfg = Join-Path $work 'run.json'
  @{ command = (Join-Path $rt 'bun\bun.exe'); prefixArgs = @('--no-install', (Join-Path $rt 'omp\node_modules\@oh-my-pi\pi-coding-agent\src\cli.ts'))
     profile = 'standin'; workingDirectory = (Join-Path $work 'proj')
     environment = @{ USERPROFILE = $home2; HOME = $home2; LOCAL_MODEL_API_KEY = 'none' } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $cfg
  $mock = Start-Process -FilePath node -ArgumentList @((Join-Path $Source 'tools\mock-model\server.mjs'), $port) -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $work 'mock.log') -RedirectStandardError (Join-Path $work 'mock.err')
  $timeline = Join-Path $work 'timeline.csv'
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $exe.FullName
  $psi.Arguments = "--config `"$cfg`" --timeline `"$timeline`" --auto-prompt `"LONGTASK 2 minutes: keep checking`""
  $psi.UseShellExecute = $false
  $psi.EnvironmentVariables['OMPGUI_RUNTIME_DIR'] = (Join-Path $rtProfile 'runtimes')
  $psi.EnvironmentVariables['APPDATA'] = (Join-Path $home2 'Roaming'); $psi.EnvironmentVariables['LOCALAPPDATA'] = (Join-Path $home2 'Local')
  $app = [System.Diagnostics.Process]::Start($psi)
  function Last { if (Test-Path -LiteralPath $timeline) { (Get-Content -LiteralPath $timeline -Tail 1) -split ',' } else { @() } }
  function WaitFor([scriptblock]$cond, [int]$seconds) { $t = [DateTime]::UtcNow.AddSeconds($seconds); while ([DateTime]::UtcNow -lt $t) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 }; return $false }
  $running = WaitFor { (Last)[2] -eq 'Running' } 180
  $app.Refresh(); $hwnd = $app.MainWindowHandle
  Check 'package-omp-rpc-prompt-streams' $running "window $hwnd; timeline: $((Last) -join ',')"
  $omps = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)" | ForEach-Object { $_.ProcessId })
  Start-Sleep -Seconds 5
  $dpi = [Gate.Win]::GetDpiForWindow($hwnd); $r = New-Object Gate.Win+RECT; [void][Gate.Win]::GetWindowRect($hwnd, [ref]$r)
  $bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1, $r.Right - $r.Left)), ([Math]::Max(1, $r.Bottom - $r.Top))
  [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size); $bmp.Save((Join-Path $work 'window.png'))
  Check 'dpi-recorded' ($dpi -gt 0) "GetDpiForWindow $dpi ($([Math]::Round($dpi / 96 * 100)) %), window $($r.Right - $r.Left)x$($r.Bottom - $r.Top), screenshot $work\window.png"
  [void][Gate.Win]::ShowWindow($hwnd, 6)                      # SW_MINIMIZE
  Start-Sleep -Seconds 3; $m0 = Last; Start-Sleep -Seconds 40; $m1 = Last
  Check 'minimized-core-folds-ui-paused' ($m1[1] -eq 'Minimized' -and [int]$m1[3] -gt [int]$m0[3] -and ([int]$m1[11] - [int]$m0[11]) -le 1) "frames $($m0[3])->$($m1[3]); UI applies $($m0[11])->$($m1[11])"
  $ended = WaitFor { (Last)[2] -eq 'Ready' } 180
  $app.Refresh()
  Check 'run-end-while-minimized-attention' ($ended -and $app.MainWindowTitle.StartsWith([string][char]0x25CF)) "title '$($app.MainWindowTitle)' (the taskbar flash is raised on the same path)"
  [void][Gate.Win]::ShowWindow($hwnd, 9); [void][Gate.Win]::SetForegroundWindow($hwnd)   # SW_RESTORE
  Start-Sleep -Seconds 2
  Check 'restore-shows-current-state' ((Last)[1] -eq 'Normal' -and (Last)[2] -eq 'Ready') "timeline: $((Last) -join ',')"
  $before = [int](Last)[9]
  [System.Windows.Forms.SendKeys]::SendWait('hello from the windows gate{ENTER}')
  $typed = WaitFor { [int](Last)[9] -gt $before -and (Last)[2] -eq 'Ready' } 60
  $sessions = Join-Path $home2 '.omp\profiles\standin\agent\sessions'
  $got = Get-ChildItem -LiteralPath $sessions -Recurse -Filter *.jsonl -ErrorAction SilentlyContinue | Select-String -SimpleMatch 'hello from the windows gate' | Select-Object -First 1
  Check 'keyboard-focus-typing-sends' ($typed -and $null -ne $got) "omp session has the typed prompt: $($null -ne $got)"
  [System.Windows.Forms.SendKeys]::SendWait('LONGTASK 2 minutes: keep checking{ENTER}')
  $streaming = WaitFor { (Last)[2] -eq 'Running' } 30
  Start-Sleep -Seconds 3; [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  $stopped = WaitFor { (Last)[2] -eq 'Ready' } 30
  $aborted = Get-ChildItem -LiteralPath $sessions -Recurse -Filter *.jsonl -ErrorAction SilentlyContinue | Select-String -SimpleMatch '"stopReason":"aborted"' | Select-Object -First 1
  Check 'esc-stops-streaming' ($streaming -and $stopped -and $null -ne $aborted) "running $streaming, ready again $stopped, omp recorded an aborted reply $($null -ne $aborted)"
  [void][Gate.Win]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE, as the title bar's X
  $exited = $app.WaitForExit(20000)
  Start-Sleep -Seconds 2
  $left = @($omps | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
  Check 'close-leaves-nothing-running' ($exited -and $left.Count -eq 0) "app exited $exited; omp processes left: $($left -join ' ')"
  if (-not $exited) { $app.Kill($true) }
  Stop-Process -Id $mock.Id -ErrorAction SilentlyContinue
  $results.Add('MANUAL 150 % and 200 % display scaling: text sharp, layout intact, no clipped buttons (Settings > Display)')
  $results.Add('MANUAL a run that ends while the window is in the background flashes its taskbar button until activated')
  $results.Add('MANUAL Attach and Open folder show the Windows file / folder dialogs; Ctrl+N, Ctrl+B, Ctrl+Enter, Alt+A / Alt+D work')
}

$results.Add("$($results.Count) lines, $fails failed; work folder $work (delete it when done)")
$results | Set-Content -LiteralPath $Out -Encoding UTF8
Write-Host "result: $Out"
if ($fails -gt 0) { exit 1 }
