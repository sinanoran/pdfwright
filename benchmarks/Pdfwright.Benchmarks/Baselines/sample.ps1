# Runs pw.cjs and reports the highest working set sampled over the whole process tree (node and the browser's
# child processes). Windows only (it reads Win32_Process). The figure is the highest of the samples, taken about
# every 150 ms, so it is a lower bound of the true peak.
#
#   npm install playwright-core            (in this folder, once)
#   $env:PW_CHROME = '<path to chrome-headless-shell.exe>'   (a Playwright "chromium_headless_shell" build)
#   pwsh sample.ps1 pw.cjs 1000
param([string]$Script, [string]$Rows)

if ($null -eq $env:PW_CHROME) {
  $root = Join-Path $env:LOCALAPPDATA 'ms-playwright'
  $shellDir = if (Test-Path $root) { Get-ChildItem $root -Directory -Filter 'chromium_headless_shell-*' | Sort-Object Name -Descending | Select-Object -First 1 }
  if ($null -eq $shellDir) { throw 'Set PW_CHROME to a chrome-headless-shell executable (npx playwright install chromium-headless-shell).' }
  $env:PW_CHROME = (Get-ChildItem $shellDir.FullName -Recurse -Filter chrome-headless-shell.exe | Select-Object -First 1).FullName
}
if ($null -eq $env:PW_CORE) { $env:PW_CORE = (Resolve-Path "$PSScriptRoot\node_modules\playwright-core").Path }

$out = [System.IO.Path]::GetTempFileName()
try {
  $p = Start-Process node -ArgumentList $Script, $Rows -PassThru -NoNewWindow -RedirectStandardOutput $out
  $max = 0
  while (-not $p.HasExited) {
    $all = Get-CimInstance Win32_Process
    $ids = @($p.Id); $changed = $true
    while ($changed) {
      $changed = $false
      foreach ($x in $all) {
        if ($ids -contains $x.ParentProcessId -and $ids -notcontains $x.ProcessId) { $ids += $x.ProcessId; $changed = $true }
      }
    }
    $sum = ($all | Where-Object { $ids -contains $_.ProcessId } | Measure-Object -Property WorkingSetSize -Sum).Sum
    if ($sum -gt $max) { $max = $sum }
    Start-Sleep -Milliseconds 150
  }
  Get-Content $out
  'highest sampled working set of the process tree: {0:N0} MB' -f ($max / 1MB)
}
finally {
  Remove-Item $out -ErrorAction SilentlyContinue
}
