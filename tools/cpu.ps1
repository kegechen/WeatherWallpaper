param([int]$Seconds = 10)
# CPU (% of all logical cores, and in "cores") + private memory of WeatherWallpaper and its WebView2 processes, per process
$all = Get-CimInstance Win32_Process
$root = $all | Where-Object { $_.Name -eq 'WeatherWallpaper.exe' } | Select-Object -First 1
$wv = $all | Where-Object { $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -like '*WeatherWallpaper\WebView2*' }
$type = @{}
foreach ($p in $wv) { $t = if ($p.CommandLine -match '--type=([\w-]+)') { $Matches[1] } else { 'browser' }; if ($p.CommandLine -match '--utility-sub-type=([\w.]+)') { $t += ':' + $Matches[1].Split('.')[-1] }; $type[[int]$p.ProcessId] = $t }
if ($root) { $type[[int]$root.ProcessId] = 'host' }
$ids = @($type.Keys)
$t0 = @{}; foreach ($p in Get-Process -Id $ids -ErrorAction SilentlyContinue) { $t0[$p.Id] = $p.TotalProcessorTime.TotalMilliseconds }
Start-Sleep -Seconds $Seconds
$cores = [Environment]::ProcessorCount
$sum = 0; $mem = 0; $rows = @()
foreach ($p in Get-Process -Id $ids -ErrorAction SilentlyContinue) {
  $d = $p.TotalProcessorTime.TotalMilliseconds - $t0[$p.Id]; $sum += $d; $mem += $p.PrivateMemorySize64
  $rows += "  {0,-28} {1,6} cpu={2,6:N1}% core  mem={3,6:N0}MB" -f $type[$p.Id], $p.Id, ($d / ($Seconds * 10)), ($p.PrivateMemorySize64 / 1MB)
}
"total cpu={0:N1}% of {1} cores (= {2:N2} core)  private_mem={3:N0}MB" -f ($sum / ($Seconds * 1000) / $cores * 100), $cores, ($sum / ($Seconds * 1000)), ($mem / 1MB)
$rows
