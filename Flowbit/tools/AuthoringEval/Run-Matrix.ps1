param(
    [Parameter(Mandatory=$true)][string]$KeyFile,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Runner = "$PSScriptRoot/bin/Release/net10.0/AuthoringEval.dll",
    [int]$PriorTrials = 0,
    [switch]$SkipBaseline
)
$ErrorActionPreference = 'Stop'
$taskTrials = [System.Collections.Generic.List[object]]::new()
$taskNumber = $PriorTrials
if ($PriorTrials -lt 0 -or $PriorTrials -gt 14) { throw 'PriorTrials must be between 0 and 14.' }
$taskOutput = [IO.Path]::GetFullPath($Output)
$taskShell = (Get-Process -Id $PID).Path
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
if (Test-Path (Join-Path $taskOutput 'matrix.json')) { throw 'Use a fresh output directory; never overwrite evaluation provenance.' }
$taskPolicy = 'strict-v1'
function Run-Trial([string]$variant, [string]$effort, [string]$fixture = 'complex', [bool]$resume = $false) {
    $script:taskNumber++
    if ($script:taskNumber -gt 14) { throw 'The initial evaluation cap is 14 workflow trials.' }
    $taskDirectory = Join-Path $taskOutput ('{0:D2}-{1}-{2}-{3}' -f $script:taskNumber, $variant, $effort, $fixture)
    New-Item -ItemType Directory -Path $taskDirectory | Out-Null
    Write-Output "Trial $script:taskNumber : $variant / $effort / $fixture (300-second limit)"
    & dotnet $Runner --key-file $KeyFile --package $Package --output $taskDirectory --variant $variant --effort $effort --fixture $fixture --fixtures "$PSScriptRoot/fixtures" --resume-test $resume.ToString().ToLowerInvariant() --policy $taskPolicy *> (Join-Path $taskDirectory 'run.log')
    $taskExit = $LASTEXITCODE
    $taskEvidencePath = Join-Path $taskDirectory 'evidence.json'
    $taskEvidence = if (Test-Path $taskEvidencePath) { Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json } else { $null }
    $taskChecksPassed = $fixture -ne 'complex'
    if ($fixture -eq 'complex' -and (Test-Path (Join-Path $taskDirectory 'result.json'))) {
        & $taskShell -NoProfile -File "$PSScriptRoot/Check-Complex.ps1" -Directory $taskDirectory -Policy $taskPolicy *> (Join-Path $taskDirectory 'checks.log')
        $taskChecksPassed = $LASTEXITCODE -eq 0
    }
    $taskTrial = [pscustomobject]@{ number = $script:taskNumber; variant = $variant; effort = $effort; fixture = $fixture; resumed = $resume; policy = $taskPolicy;
        passed = ($taskExit -eq 0 -and $null -ne $taskEvidence -and $taskEvidence.passed -and $taskChecksPassed); seconds = $taskEvidence.seconds; directory = $taskDirectory }
    $script:taskTrials.Add($taskTrial)
    $script:taskTrials | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'matrix.json')
    Write-Output ("Completed: pass={0}, seconds={1}" -f $taskTrial.passed, $taskTrial.seconds)
}
if (!$SkipBaseline) { Run-Trial 'current' 'max' }
foreach ($taskEffort in @('low', 'high', 'max')) {
    Run-Trial 'optimized' $taskEffort
}
$taskFinalists = @()
foreach ($taskVariant in @('optimized')) {
    $taskBest = @($taskTrials | Where-Object { $_.variant -eq $taskVariant -and $_.passed } | Sort-Object seconds | Select-Object -First 1)
    if ($taskBest.Count -gt 0) { $taskFinalists += $taskBest[0] }
}
for ($taskRepeat = 0; $taskRepeat -lt 2; $taskRepeat++) {
    foreach ($taskFinalist in $taskFinalists) { Run-Trial $taskFinalist.variant $taskFinalist.effort }
}
$taskPassing = @()
foreach ($taskFinalist in $taskFinalists) {
    $taskRuns = @($taskTrials | Where-Object { $_.variant -eq $taskFinalist.variant -and $_.effort -eq $taskFinalist.effort -and $_.fixture -eq 'complex' })
    if ($taskRuns.Count -eq 3 -and @($taskRuns | Where-Object { !$_.passed }).Count -eq 0) {
        $taskPassing += [pscustomobject]@{ variant = $taskFinalist.variant; effort = $taskFinalist.effort; medianSeconds = @($taskRuns.seconds | Sort-Object)[1] }
    }
}
$taskOptimized = $taskPassing | Where-Object variant -eq 'optimized'
$taskWinner = $taskOptimized
if ($null -ne $taskWinner -and $taskNumber + 3 -le 14) {
    Run-Trial $taskWinner.variant $taskWinner.effort 'simple'
    Run-Trial $taskWinner.variant $taskWinner.effort 'modify'
    Run-Trial $taskWinner.variant $taskWinner.effort 'complex' $true
    if (@($taskTrials | Select-Object -Last 3 | Where-Object { !$_.passed }).Count -gt 0) { $taskWinner = $null }
}
elseif ($null -ne $taskWinner) { $taskWinner = $null; Write-Output 'The trial cap leaves insufficient room for final validation; no candidate will be promoted.' }
[pscustomobject]@{ selected = $taskWinner; passingFinalists = $taskPassing; workflowTrials = $taskNumber; cap = 14;
    decision = $(if ($null -eq $taskWinner) { 'Keep current defaults: the acceptance gates were not met.' } else { 'Candidate passes live gates; automated and browser verification must also pass before promotion.' })
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'decision.json')
