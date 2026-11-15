param([int]$X = 700, [int]$Y = 380, [int]$W = 760, [int]$H = 420, [int]$MoveAfter = 0, [int]$Life = 30, [string]$Shot = "")
# Opens a plain test window (for eave effects), optionally moves it after N seconds and takes a screenshot right after the move.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object Windows.Forms.Form
$f.Text = "Eave test"; $f.StartPosition = "Manual"; $f.Location = New-Object Drawing.Point($X, $Y); $f.Size = New-Object Drawing.Size($W, $H)
$f.BackColor = [Drawing.Color]::FromArgb(32, 32, 36)
$t = New-Object Windows.Forms.Timer; $t.Interval = 1000; $script:n = 0
$t.Add_Tick({
  $script:n++
  if ($MoveAfter -gt 0 -and $script:n -eq $MoveAfter) {
    $f.Location = New-Object Drawing.Point(($X + 60), ($Y + 10))
    if ($Shot) {
      Start-Sleep -Milliseconds 120
      $b = New-Object Drawing.Bitmap(1920, 1080); $g = [Drawing.Graphics]::FromImage($b)
      $g.CopyFromScreen(0, 0, 0, 0, $b.Size); $b.Save($Shot)
    }
  }
  if ($script:n -ge $Life) { $f.Close() }
})
$t.Start()
[void]$f.ShowDialog()
