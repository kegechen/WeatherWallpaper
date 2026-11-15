# Memory breakdown of WeatherWallpaper + its WebView2 processes:
#   WSPriv = private working set (what Task Manager shows in "Memory"), Commit = private bytes, GPU = dedicated + shared GPU memory
$all = Get-CimInstance Win32_Process
$root = $all | Where-Object { $_.Name -eq 'WeatherWallpaper.exe' } | Select-Object -First 1
$wv = $all | Where-Object { $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -like '*WeatherWallpaper\WebView2*' }
$type = @{}
foreach ($p in $wv) { $t = if ($p.CommandLine -match '--type=([\w-]+)') { $Matches[1] } else { 'browser' }; if ($p.CommandLine -match '--utility-sub-type=([\w.]+)') { $t += ':' + $Matches[1].Split('.')[-1] }; $type[[int]$p.ProcessId] = $t }
if ($root) { $type[[int]$root.ProcessId] = 'host' }
$gpu = @{}
try {
  foreach ($s in (Get-Counter '\GPU Process Memory(*)\Dedicated Usage', '\GPU Process Memory(*)\Shared Usage' -ErrorAction Stop).CounterSamples) {
    if ($s.InstanceName -match 'pid_(\d+)_') { $id = [int]$Matches[1]; $gpu[$id] = $gpu[$id] + $s.CookedValue }
  }
} catch {}
$perf = Get-CimInstance Win32_PerfRawData_PerfProc_Process | Where-Object { $type.ContainsKey([int]$_.IDProcess) }
$tw = 0; $tc = 0; $tg = 0
$rows = foreach ($p in $perf) {
  $id = [int]$p.IDProcess; $w = $p.WorkingSetPrivate / 1MB; $c = $p.PrivateBytes / 1MB; $g = $gpu[$id] / 1MB
  $tw += $w; $tc += $c; $tg += $g
  "  {0,-28} {1,6}  WSPriv={2,5:N0}MB  Commit={3,5:N0}MB  GPU={4,5:N0}MB" -f $type[$id], $id, $w, $c, $g
}
"total WSPriv={0:N0}MB  Commit={1:N0}MB  GPU={2:N0}MB" -f $tw, $tc, $tg
$rows
