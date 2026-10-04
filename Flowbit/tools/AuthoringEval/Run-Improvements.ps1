param(
    [Parameter(Mandatory=$true)][string]$BaselineRunner,
    [Parameter(Mandatory=$true)][string]$CacheRunner,
    [Parameter(Mandatory=$true)][string]$Runner,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$KeyFile,
    [ValidateRange(30,3600)][int]$TimeoutSeconds = 600,
    [ValidateRange(0,14)][int]$PriorTrials = 0,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
# Planning is the default. Only an explicitly authorized invocation should use -Execute.
$taskOutput = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a fresh output directory; failures and provenance must be retained.' }
if ($PriorTrials + 11 -gt 14) { throw 'Insufficient capacity for the complete 11-trial comparison within the 14-trial cap.' }
if ($Execute -and (!(Test-Path -LiteralPath $KeyFile -PathType Leaf))) { throw 'Execution requires a private key file.' }
$taskShell = (Get-Process -Id $PID).Path
$taskConfigurations = @(
    @{ name = 'prechange'; runner = [IO.Path]::GetFullPath($BaselineRunner); review = $false; workers = 1; count = 1 },
    @{ name = 'cache-fixed'; runner = [IO.Path]::GetFullPath($CacheRunner); review = $false; workers = 1; count = 1 },
    @{ name = 'reviewed-serial'; runner = [IO.Path]::GetFullPath($Runner); review = $true; workers = 1; count = 3 },
    @{ name = 'reviewed-parallel'; runner = [IO.Path]::GetFullPath($Runner); review = $true; workers = 2; count = 3 }
)
$taskPackage = [IO.Path]::GetFullPath($Package)
function Fingerprint([string]$root) {
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "Missing frozen directory: $root" }
    @(Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($root, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
foreach ($taskConfig in $taskConfigurations) {
    if (!(Test-Path -LiteralPath $taskConfig.runner -PathType Leaf)) { throw "Missing runner: $($taskConfig.runner)" }
    $taskConfig.files = Fingerprint (Split-Path -Parent $taskConfig.runner)
}
$taskPackageFiles = Fingerprint $taskPackage
$taskFixtureFiles = Fingerprint "$PSScriptRoot/fixtures"
New-Item -ItemType Directory -Path $taskOutput | Out-Null
[pscustomobject]@{ executionRequested = [bool]$Execute; createdUtc = [DateTime]::UtcNow; priorTrials = $PriorTrials; plannedTrials = 11; cap = 14;
    model = 'glm-5.3-flash'; endpoint = 'https://opencode.ai/zen/v1'; variant = 'optimized'; effort = 'max'; seconds = $TimeoutSeconds;
    package = $taskPackage; packageFiles = $taskPackageFiles; fixtureFiles = $taskFixtureFiles; configurations = $taskConfigurations;
    finalValidation = @('simple', 'modify', 'complex Cancel/Continue'); policies = @('strict-v1','functional-v2')
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'plan.json')
if (!$Execute) { Write-Output 'Recorded the 11-trial plan and hashes. No provider calls. Reinvoke with a fresh output and -Execute only after live-call authorization.'; exit 0 }
$taskTrials = [System.Collections.Generic.List[object]]::new()
$taskNumber = $PriorTrials
function Assert-Frozen($config) {
    $current = Fingerprint (Split-Path -Parent $config.runner)
    if (($current | ConvertTo-Json -Compress) -cne ($config.files | ConvertTo-Json -Compress) -or
        ((Fingerprint $taskPackage) | ConvertTo-Json -Compress) -cne ($taskPackageFiles | ConvertTo-Json -Compress) -or
        ((Fingerprint "$PSScriptRoot/fixtures") | ConvertTo-Json -Compress) -cne ($taskFixtureFiles | ConvertTo-Json -Compress)) { throw 'Frozen inputs changed; preserve this round and start a new comparison.' }
}
function Run-Trial($config, [string]$fixture = 'complex', [bool]$resume = $false) {
    Assert-Frozen $config
    $script:taskNumber++
    $directory = Join-Path $taskOutput ('{0:D2}-{1}-{2}' -f $script:taskNumber, $config.name, $fixture)
    New-Item -ItemType Directory -Path $directory | Out-Null
    # Record a started trial before transport so an interrupted process still consumes capacity.
    $trial = [pscustomobject]@{ number = $script:taskNumber; configuration = $config.name; fixture = $fixture; resume = $resume;
        status = 'started'; passed = $false; seconds = $null; directory = $directory; policies = @{}; run = $null }
    $script:taskTrials.Add($trial)
    $script:taskTrials | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'matrix.json')
    & dotnet $config.runner --key-file $KeyFile --package $taskPackage --output $directory --variant optimized --effort max --fixture $fixture --fixtures "$PSScriptRoot/fixtures" --timeout-seconds $TimeoutSeconds --resume-test $resume.ToString().ToLowerInvariant() --policy strict-v1 --review $config.review.ToString().ToLowerInvariant() --analysis-workers $config.workers *> (Join-Path $directory 'run.log')
    $exit = $LASTEXITCODE
    $evidence = if (Test-Path (Join-Path $directory 'evidence.json')) { Get-Content -LiteralPath (Join-Path $directory 'evidence.json') -Raw | ConvertFrom-Json } else { $null }
    foreach ($policy in @('strict-v1','functional-v2')) {
        $checkOutput = Join-Path $directory $policy
        New-Item -ItemType Directory -Path $checkOutput | Out-Null
        if (!(Test-Path (Join-Path $directory 'result.json'))) { $trial.policies[$policy] = $false; continue }
        if ($fixture -eq 'complex') {
            & $taskShell -NoProfile -File "$PSScriptRoot/Check-Complex.ps1" -Directory $directory -Policy $policy -OutputDirectory $checkOutput *> (Join-Path $checkOutput 'checks.log')
        } else {
            & dotnet $Runner --check-only (Join-Path $directory 'result.json') --output $checkOutput --fixture $fixture --policy $policy *> (Join-Path $checkOutput 'checks.log')
        }
        $trial.policies[$policy] = $LASTEXITCODE -eq 0
    }
    $trial.status = 'completed'; $trial.seconds = $evidence.seconds; $trial.run = $evidence.run
    $trial.passed = $exit -eq 0 -and $null -ne $evidence -and $evidence.passed -and $evidence.seconds -le $TimeoutSeconds -and $trial.policies['strict-v1']
    $script:taskTrials | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'matrix.json')
    Write-Output ("{0}: pass={1}, seconds={2}" -f (Split-Path -Leaf $directory), $trial.passed, $trial.seconds)
}
foreach ($taskConfig in $taskConfigurations) { for ($taskRepeat = 0; $taskRepeat -lt $taskConfig.count; $taskRepeat++) { Run-Trial $taskConfig } }
$taskCandidates = @()
foreach ($taskConfig in $taskConfigurations | Where-Object review) {
    $runs = @($taskTrials | Where-Object configuration -eq $taskConfig.name)
    if ($runs.Count -eq 3 -and @($runs | Where-Object { !$_.passed }).Count -eq 0) {
        $taskCandidates += [pscustomobject]@{ configuration = $taskConfig; medianSeconds = @($runs.seconds | Sort-Object)[1] }
    }
}
$taskWinner = $taskCandidates | Sort-Object medianSeconds | Select-Object -First 1
if ($null -ne $taskWinner) {
    Run-Trial $taskWinner.configuration 'simple'
    Run-Trial $taskWinner.configuration 'modify'
    Run-Trial $taskWinner.configuration 'complex' $true
    if (@($taskTrials | Select-Object -Last 3 | Where-Object { !$_.passed }).Count -gt 0) { $taskWinner = $null }
}
[pscustomobject]@{ selected = $taskWinner.configuration.name; workflowTrials = $taskNumber; cap = 14; timeoutSeconds = $TimeoutSeconds;
    decision = $(if ($null -eq $taskWinner) { 'No reviewed configuration qualified. Keep defaults.' } else { 'Candidate passed the live gate. Defaults remain unchanged; promotion is a separate decision.' })
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'decision.json')
