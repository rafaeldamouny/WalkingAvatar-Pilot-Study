param(
    [ValidateSet('Devices','InstallExperiment','InstallTemplate','CopyMountCalibration','PullLogs','SetConnection')]
    [string]$Action = 'Devices',
    [string]$Serial,
    [string]$TemplateApk,
    [string]$PhoneIp
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$taskAdb = 'C:\Program Files\Unity\Hub\Editor\6000.2.4f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $taskAdb)) { throw "Unity Android tools missing: $taskAdb" }
function Invoke-QuestAdb([string[]]$AdbArguments) {
    $taskDeviceArgs = @()
    if ($Serial) { $taskDeviceArgs = @('-s', $Serial) }
    & $taskAdb @taskDeviceArgs @AdbArguments
    if ($LASTEXITCODE -ne 0) { throw "Headset command failed (exit $LASTEXITCODE). Check connection and USB debugging permission." }
}
$taskFiles = '/sdcard/Android/data/com.amplitlab.neonwalking/files'
switch ($Action) {
    'Devices' { Invoke-QuestAdb -AdbArguments @('devices','-l') }
    'InstallExperiment' {
        $taskApk = Join-Path $taskProjectRoot 'Builds\NeonWalkingExperiment.apk'
        if (-not (Test-Path -LiteralPath $taskApk)) { throw 'Build the Quest APK in Unity first.' }
        Invoke-QuestAdb -AdbArguments @('install','-r',$taskApk)
        Write-Host 'Launch Neon Walking Experiment from Unknown Sources. Close it before copying calibration.'
    }
    'InstallTemplate' {
        if (-not $TemplateApk -or -not (Test-Path -LiteralPath $TemplateApk)) { throw 'Provide -TemplateApk with the downloaded official Quest 3 template APK path.' }
        Invoke-QuestAdb -AdbArguments @('install','-r',(Resolve-Path -LiteralPath $TemplateApk).Path)
    }
    'CopyMountCalibration' {
        $taskCalibrationDir = Join-Path $taskProjectRoot 'Calibration'
        New-Item -ItemType Directory -Path $taskCalibrationDir -Force | Out-Null
        $taskCalibrationConfig = Join-Path $taskCalibrationDir 'config.json'
        Invoke-QuestAdb -AdbArguments @('pull','/sdcard/Android/data/org.MixedRealityToolkit.MRTK3Sample/files/config.json',$taskCalibrationConfig)
        $taskConfig = Get-Content -LiteralPath $taskCalibrationConfig -Raw | ConvertFrom-Json
        if (-not $taskConfig.sensorCalibration.offset.position -or -not $taskConfig.sensorCalibration.offset.rotation) { throw 'Downloaded file has no valid sensorCalibration.offset. Complete and save official mount calibration first.' }
        Invoke-QuestAdb -AdbArguments @('push',$taskCalibrationConfig,"$taskFiles/config.json")
        Write-Host 'Calibration copied. Relaunch the experiment and validate pointer accuracy before enabling Mount Calibration Verified in Unity.'
    }
    'PullLogs' {
        $taskExports = Join-Path $taskProjectRoot 'ResearchExports'
        New-Item -ItemType Directory -Path $taskExports -Force | Out-Null
        Invoke-QuestAdb -AdbArguments @('pull',"$taskFiles/ResearchLogs",$taskExports)
        Write-Host "Logs copied to $taskExports"
    }
    'SetConnection' {
        $taskParsedIp = $null
        if (-not [System.Net.IPAddress]::TryParse($PhoneIp,[ref]$taskParsedIp)) { throw 'Provide the S25 IPv4 address shown by Neon Companion using -PhoneIp.' }
        $taskCalibrationDir = Join-Path $taskProjectRoot 'Calibration'
        New-Item -ItemType Directory -Path $taskCalibrationDir -Force | Out-Null
        $taskConnectionConfig = Join-Path $taskCalibrationDir 'config-connection.json'
        Invoke-QuestAdb -AdbArguments @('pull',"$taskFiles/config.json",$taskConnectionConfig)
        $taskConfig = Get-Content -LiteralPath $taskConnectionConfig -Raw | ConvertFrom-Json
        $taskConfig.rtspSettings.autoIp = $false
        $taskConfig.rtspSettings.ip = $PhoneIp
        $taskConfig.rtspSettings.useUdp = $true
        $taskConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskConnectionConfig -Encoding UTF8
        Invoke-QuestAdb -AdbArguments @('push',$taskConnectionConfig,"$taskFiles/config.json")
        Write-Host 'Connection updated, mount offset preserved. Restart the experiment app.'
    }
}

