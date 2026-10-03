param(
    [Parameter(Mandatory=$true)][string]$KeyFile,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Runner = "$PSScriptRoot/bin/Release/net10.0/AuthoringEval.dll",
    [int]$PriorTrials = 0,
    [switch]$SkipBaseline,
    [ValidateSet('Comparison','FinalizeFramework')][string]$Mode = 'Comparison',
    [string]$RetainedResults
)
$ErrorActionPreference = 'Stop'
$taskTrials = [System.Collections.Generic.List[object]]::new()
$taskNumber = $PriorTrials
if ($PriorTrials -lt 0 -or $PriorTrials -gt 14) { throw 'PriorTrials must be between 0 and 14.' }
$taskOutput = [IO.Path]::GetFullPath($Output)
$taskShell = (Get-Process -Id $PID).Path
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
if (Test-Path (Join-Path $taskOutput 'matrix.json')) { throw 'Use a fresh output directory; never overwrite evaluation provenance.' }
$taskPolicy = if ($Mode -eq 'FinalizeFramework') { 'functional-v2' } else { 'strict-v1' }
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
if ($Mode -eq 'FinalizeFramework') {
    if ($PriorTrials -ne 11 -or !$RetainedResults -or $SkipBaseline) { throw 'FinalizeFramework requires PriorTrials=11 and RetainedResults; do not use SkipBaseline.' }
    $taskReassessments = @()
    foreach ($taskName in @('07-agent-framework-high-complex','10-agent-framework-high-complex','11-agent-framework-high-complex')) {
        $taskSource = Join-Path $RetainedResults $taskName
        $taskEvidence = Get-Content -LiteralPath (Join-Path $taskSource 'evidence.json') -Raw | ConvertFrom-Json
        if ($taskEvidence.variant -ne 'agent-framework' -or $taskEvidence.effort -ne 'high' -or $taskEvidence.fixture -ne 'complex' -or $taskEvidence.resumed -or !$taskEvidence.passed -or $taskEvidence.seconds -gt 300) { throw "Retained trial $taskName does not meet the structural/deadline prerequisites." }
        $taskRecheck = Join-Path $taskOutput ('reassessment/' + $taskName)
        New-Item -ItemType Directory -Path $taskRecheck | Out-Null
        & $taskShell -NoProfile -File "$PSScriptRoot/Check-Complex.ps1" -Directory $taskSource -OutputDirectory $taskRecheck -Policy $taskPolicy *> (Join-Path $taskRecheck 'checks.log')
        $taskPassed = $LASTEXITCODE -eq 0
        $taskReassessments += [pscustomobject]@{ trial = $taskName; passed = $taskPassed; seconds = $taskEvidence.seconds;
            contractHash = $taskEvidence.contractHash; source = [IO.Path]::GetFullPath($taskSource);
            resultHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $taskSource 'result.json')).Hash }
    }
    $taskReassessments | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'reassessments.json')
    if (@($taskReassessments | Where-Object { !$_.passed }).Count -gt 0) { throw 'Retained functional reassessment failed; no live trials or promotion.' }
    @('AuthoringEval.dll','Flowbit.Service.dll','Flowbit.Infrastructure.dll') | ForEach-Object {
        Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path $Runner) $_) | Select-Object Path,Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'runner-hashes.json')
    Run-Trial 'agent-framework' 'high' 'simple'
    Run-Trial 'agent-framework' 'high' 'modify'
    Run-Trial 'agent-framework' 'high' 'complex' $true
    $taskWinner = $null
    if (@($taskTrials | Where-Object { !$_.passed }).Count -eq 0) { $taskWinner = [pscustomobject]@{ variant = 'agent-framework'; effort = 'high' } }
    [pscustomobject]@{ selected = $taskWinner; policy = $taskPolicy; selection = 'targeted-framework-adoption'; workflowTrials = $taskNumber; priorTrials = $PriorTrials; cap = 14;
        decision = $(if ($null -eq $taskWinner) { 'Keep current defaults: functional acceptance failed.' } else { 'Live functional gates passed; automated and shipped-default browser checks are required before release.' })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'decision.json')
    exit
}
if (!$SkipBaseline) { Run-Trial 'current' 'max' }
foreach ($taskEffort in @('low', 'high', 'max')) {
    Run-Trial 'optimized' $taskEffort
    Run-Trial 'agent-framework' $taskEffort
}
$taskFinalists = @()
foreach ($taskVariant in @('optimized', 'agent-framework')) {
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
$taskFramework = $taskPassing | Where-Object variant -eq 'agent-framework'
$taskWinner = $null
if ($null -ne $taskFramework -and ($null -eq $taskOptimized -or $taskFramework.medianSeconds -le $taskOptimized.medianSeconds * 0.8)) { $taskWinner = $taskFramework }
elseif ($null -ne $taskOptimized) { $taskWinner = $taskOptimized }
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
