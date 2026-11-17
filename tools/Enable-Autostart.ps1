$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectDir 'FanRgbStudio.exe'
if (-not (Test-Path -LiteralPath $appPath)) { $appPath = Join-Path $projectDir 'dist\FanRgbStudio.exe' }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    $elevated = Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $elevated.WaitForExit()
    exit $elevated.ExitCode
}
$dataDir = Join-Path $env:LOCALAPPDATA 'SunuongFanStudio'
$resultPath = Join-Path $dataDir 'autostart-registration.json'
$taskName = 'SunuongFanStudio'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
try {
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "Application missing: $appPath" }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute $appPath -Argument '--display --background --rgb' -WorkingDirectory (Split-Path -Parent $appPath)
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $trigger.Delay = 'PT15S'
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Start Sunuong Fan Studio in the background at user logon, with fan profiles, PA120 display and RGB engine.'
    Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
    $registered = Get-ScheduledTask -TaskName $taskName
    $xml = Export-ScheduledTask -TaskName $taskName
    [IO.File]::WriteAllText((Join-Path $dataDir 'autostart-task.xml'), $xml)
    [ordered]@{ Success = $true; TaskName = $registered.TaskName; State = [string]$registered.State; User = $identity; Execute = $registered.Actions[0].Execute; Arguments = $registered.Actions[0].Arguments; RunLevel = [string]$registered.Principal.RunLevel; LogonType = [string]$registered.Principal.LogonType; Timestamp = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
} catch {
    [ordered]@{ Success = $false; Error = $_.Exception.Message; Timestamp = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
