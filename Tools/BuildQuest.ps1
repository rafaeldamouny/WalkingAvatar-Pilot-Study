param([string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.2.4f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$taskProjectRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$taskBuildTemp = Join-Path $taskProjectRoot '.build-temp'
New-Item -ItemType Directory -Path $taskBuildTemp -Force | Out-Null
$taskBuildLog = Join-Path $taskProjectRoot 'IntegrationEvidence\android-build-editor.log'
$taskSavedTemp = $env:TEMP
$taskSavedTmp = $env:TMP
try {
    # Java's Windows local sockets require a drive-qualified TEMP inherited by the daemon.
    $env:TEMP = $taskBuildTemp
    $env:TMP = $taskBuildTemp
    $taskBuildArguments = @('-batchmode','-quit','-buildTarget','Android','-projectPath',('"'+$taskProjectRoot+'"'),
        '-executeMethod','WalkingEyeExperiment.Editor.IntegrationBuild.BuildQuest','-logFile',('"'+$taskBuildLog+'"'))
    $taskUnityRun = Start-Process -FilePath $UnityEditor -ArgumentList $taskBuildArguments -WindowStyle Hidden -Wait -PassThru
    if ($taskUnityRun.ExitCode -ne 0) { throw "Quest build failed (exit $($taskUnityRun.ExitCode)). See $taskBuildLog" }
    Write-Host ('Quest APK: '+(Join-Path $taskProjectRoot 'Builds\NeonWalkingExperiment.apk'))
} finally {
    $env:TEMP = $taskSavedTemp
    $env:TMP = $taskSavedTmp
}
