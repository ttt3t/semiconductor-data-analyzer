param([string]$Exe, [string]$Fixtures)
$ErrorActionPreference = 'Stop'
# This integration test seeds the real Windows profile used by the published
# process. Only run it on a disposable GitHub Actions Windows runner.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This test requires a disposable GitHub Actions runner.' }
$taskExe = (Resolve-Path -LiteralPath $Exe).Path
$taskFixtures = (Resolve-Path -LiteralPath $Fixtures).Path
$taskProfile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SemiconductorCsvAnalyzer'
$taskBackups = @{}
$taskSeed = @{
    'layout.settings.json' = Join-Path $taskFixtures 'layout.settings.json'
    'ui.preferences.json' = Join-Path $taskFixtures 'ui.preferences.json'
    'ProductLibrary/catalog.json' = Join-Path $taskFixtures 'legacy-catalog-2100.json'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
try {
    foreach ($taskName in $taskSeed.Keys) {
        $taskPath = Join-Path $taskProfile $taskName
        $taskBackups[$taskPath] = if (Test-Path -LiteralPath $taskPath) { [IO.File]::ReadAllBytes($taskPath) } else { $null }
        New-Item -ItemType Directory -Path (Split-Path $taskPath) -Force | Out-Null
        Copy-Item -LiteralPath $taskSeed[$taskName] -Destination $taskPath -Force
    }
    $taskCatalog = Join-Path $taskProfile 'ProductLibrary/catalog.json'
    $taskHash = (Get-FileHash -LiteralPath $taskCatalog).Hash
    # config.ini ships next to the EXE, as in an in-place upgrade.
    if (!(Test-Path -LiteralPath (Join-Path (Split-Path $taskExe) 'config.ini'))) { throw 'Portable INI fixture is missing.' }
    foreach ($taskMode in @('management', 'analyzer')) {
        $taskProcess = if ($taskMode -eq 'analyzer') { Start-Process -FilePath $taskExe -ArgumentList '--analyzer' -PassThru } else { Start-Process -FilePath $taskExe -PassThru }
        try {
            $taskReady = $false
            for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
                Start-Sleep -Milliseconds 250
                $taskProcess.Refresh()
                if ($taskProcess.HasExited) { throw "Published EXE exited during $taskMode startup: $($taskProcess.ExitCode)" }
                if ($taskProcess.MainWindowHandle -eq 0) { continue }
                $taskWindow = [System.Windows.Automation.AutomationElement]::FromHandle($taskProcess.MainWindowHandle)
                if ($taskMode -eq 'management') {
                    $taskCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'DgFiles')
                    $taskGrid = $taskWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $taskCondition)
                    if ($null -eq $taskGrid) { continue }
                    $taskPattern = $taskGrid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern)
                    $taskReady = $taskPattern.Current.RowCount -eq 2100
                } else { $taskReady = $taskProcess.MainWindowTitle.Contains('V2.1.1') }
                if ($taskReady) { break }
            }
            if (!$taskReady) { throw "Published EXE did not render the expected $taskMode window." }
            Start-Sleep -Seconds 2
            $taskProcess.Refresh()
            if ($taskProcess.HasExited) { throw "Published EXE crashed after rendering $taskMode." }
            if (!$taskProcess.CloseMainWindow() -or !$taskProcess.WaitForExit(10000)) { throw "Published EXE did not close normally: $taskMode" }
            if ($taskProcess.ExitCode -ne 0) { throw "Published EXE failed: $taskMode, exit $($taskProcess.ExitCode)" }
            if ((Get-FileHash -LiteralPath $taskCatalog).Hash -ne $taskHash) { throw 'Startup changed the original product catalog.' }
            Write-Host "PASS published single EXE $taskMode opens and closes with legacy profile, 2100 registrations and adjacent INI"
        } finally {
            if (!$taskProcess.HasExited) { $taskProcess.Kill(); $taskProcess.WaitForExit() }
            $taskProcess.Dispose()
        }
    }
} finally {
    foreach ($taskPath in $taskBackups.Keys) {
        if ($null -eq $taskBackups[$taskPath]) { Remove-Item -LiteralPath $taskPath -Force -ErrorAction SilentlyContinue }
        else { [IO.File]::WriteAllBytes($taskPath, $taskBackups[$taskPath]) }
    }
}
