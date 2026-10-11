$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($role in @('Admin', 'Agent')) {
  $dir = Join-Path $env:TEMP "SchoolPcInstallerTest-$role"
  $exe = Join-Path $dir "SchoolPc.$role.exe"
  $setup = (Resolve-Path "release/SchoolPc.$role.Setup.exe").Path
  $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"")
  if ($role -eq 'Agent') { $arguments += '/TASKS=autostart' }
  $install = Start-Process $setup -ArgumentList $arguments -Wait -PassThru
  if ($install.ExitCode -ne 0 -or -not (Test-Path $exe) -or -not (Test-Path "$dir/installed.ini")) { throw "Installation failed: $role ($($install.ExitCode))" }
  if ($role -eq 'Agent') {
    $command = Get-ItemPropertyValue $runKey -Name 'SchoolPc.Agent'
    if ($command -ne "`"$exe`" --tray") { throw 'Agent startup command mismatch' }
  }
  $before = (Get-FileHash $exe).Hash
  $install = Start-Process $setup -ArgumentList $arguments -Wait -PassThru
  if ($install.ExitCode -ne 0 -or (Get-FileHash "$exe.previous").Hash -ne $before) { throw "Installation backup failed: $role" }
  $remove = Start-Process "$dir/unins000.exe" -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
  if ($remove.ExitCode -ne 0 -or (Test-Path $exe) -or (Test-Path "$exe.previous")) { throw "Uninstall failed: $role" }
  if ($role -eq 'Agent' -and (Get-ItemProperty $runKey).PSObject.Properties.Name -contains 'SchoolPc.Agent') { throw 'Agent autorun survived uninstall' }
  Write-Host "PASS $role install, same-path backup and uninstall"
}
