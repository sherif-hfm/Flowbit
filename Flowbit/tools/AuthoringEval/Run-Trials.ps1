param(
    [Parameter(Mandatory=$true)][string]$Runner,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$KeyFile,
    [string[]]$Trials = @('complex', 'complex', 'complex', 'simple', 'modify', 'complex-resume'),
    [ValidateRange(30,3600)][int]$TimeoutSeconds = 1800,
    [ValidateRange(1,20)][int]$Cap = 6,
    [ValidateRange(0,20)][int]$PriorTrials = 0,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
# Planning is the default. Only an explicitly authorized invocation should use -Execute: every trial makes billed provider calls.
$taskOutput = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a fresh output directory; failures and provenance must be retained.' }
# pwsh -File passes "-Trials simple,modify" as one string.
$Trials = @($Trials | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($taskName in $Trials) {
    if ($taskName -notin @('complex', 'simple', 'modify', 'complex-resume')) { throw "Unknown trial '$taskName'. Use complex, simple, modify or complex-resume." }
}
if ($PriorTrials + $Trials.Count -gt $Cap) { throw "$($Trials.Count) trials after $PriorTrials prior trials exceed the cap of $Cap. Never reset the count to hide failures." }
if ($Execute -and (!$KeyFile -or !(Test-Path -LiteralPath $KeyFile -PathType Leaf))) { throw 'Execution requires a private key file.' }
$taskShell = (Get-Process -Id $PID).Path
$taskRunner = [IO.Path]::GetFullPath($Runner)
$taskPackage = [IO.Path]::GetFullPath($Package)
$taskFixtures = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'fixtures'))
if (!(Test-Path -LiteralPath $taskRunner -PathType Leaf)) { throw "Missing runner: $taskRunner" }
function Fingerprint([string]$root) {
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "Missing frozen directory: $root" }
    $full = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\', '/')
    @(Get-ChildItem -LiteralPath $full -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($full.Length).TrimStart('\', '/').Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Compact($files) { $files | ConvertTo-Json -Compress -Depth 4 }
$taskRunnerFiles = Fingerprint (Split-Path -Parent $taskRunner)
$taskPackageFiles = Fingerprint $taskPackage
$taskFixtureFiles = Fingerprint $taskFixtures
New-Item -ItemType Directory -Path $taskOutput | Out-Null
[pscustomobject]@{ executionRequested = [bool]$Execute; createdUtc = [DateTime]::UtcNow; engine = 'agent'; trials = $Trials; priorTrials = $PriorTrials; cap = $Cap;
    model = 'glm-5.3-flash'; reasoningEffort = 'high'; endpoint = 'https://opencode.ai/zen/v1'; seconds = $TimeoutSeconds;
    runner = $taskRunner; runnerFiles = $taskRunnerFiles; package = $taskPackage; packageFiles = $taskPackageFiles; fixtureFiles = $taskFixtureFiles;
    policies = @('strict-v1', 'functional-v2')
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'plan.json')
if (!$Execute) { Write-Output "Recorded the $($Trials.Count)-trial plan and hashes. No provider calls. Reinvoke with a fresh output and -Execute only after live-call authorization."; exit 0 }

$taskResults = [System.Collections.Generic.List[object]]::new()
$taskNumber = $PriorTrials
function Assert-Frozen {
    if ((Compact (Fingerprint (Split-Path -Parent $taskRunner))) -cne (Compact $taskRunnerFiles) -or
        (Compact (Fingerprint $taskPackage)) -cne (Compact $taskPackageFiles) -or
        (Compact (Fingerprint $taskFixtures)) -cne (Compact $taskFixtureFiles)) { throw 'Frozen inputs changed; preserve this round and start a new one.' }
}
foreach ($taskName in $Trials) {
    Assert-Frozen
    $taskNumber++
    $fixture = $taskName -replace '-resume$', ''
    $resume = $taskName -like '*-resume'
    $directory = Join-Path $taskOutput ('{0:D2}-{1}' -f $taskNumber, $taskName)
    New-Item -ItemType Directory -Path $directory | Out-Null
    # Record a started trial before transport so an interrupted process still counts toward the cap.
    $trial = [pscustomobject]@{ number = $taskNumber; trial = $taskName; fixture = $fixture; resume = $resume; status = 'started'; passed = $false;
        seconds = $null; kind = $null; code = $null; directory = $directory; policies = @{}; run = $null }
    $taskResults.Add($trial)
    $taskResults | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'trials.json')
    & dotnet $taskRunner --key-file $KeyFile --package $taskPackage --output $directory --fixture $fixture --fixtures $taskFixtures --timeout-seconds $TimeoutSeconds --resume-test $resume.ToString().ToLowerInvariant() --policy strict-v1 *> (Join-Path $directory 'run.log')
    $exit = $LASTEXITCODE
    $evidence = if (Test-Path (Join-Path $directory 'evidence.json')) { Get-Content -LiteralPath (Join-Path $directory 'evidence.json') -Raw | ConvertFrom-Json } else { $null }
    foreach ($policy in @('strict-v1', 'functional-v2')) {
        $checkOutput = Join-Path $directory $policy
        New-Item -ItemType Directory -Path $checkOutput | Out-Null
        if (!(Test-Path (Join-Path $directory 'result.json'))) { $trial.policies[$policy] = $false; continue }
        if ($fixture -eq 'complex') {
            & $taskShell -NoProfile -File "$PSScriptRoot/Check-Complex.ps1" -Directory $directory -Policy $policy -OutputDirectory $checkOutput *> (Join-Path $checkOutput 'checks.log')
        } else {
            & dotnet $taskRunner --check-only (Join-Path $directory 'result.json') --output $checkOutput --fixture $fixture --policy $policy *> (Join-Path $checkOutput 'checks.log')
        }
        $trial.policies[$policy] = $LASTEXITCODE -eq 0
    }
    $trial.status = 'completed'; $trial.seconds = $evidence.seconds; $trial.kind = $evidence.kind; $trial.code = $evidence.code; $trial.run = $evidence.run
    $trial.passed = $exit -eq 0 -and $null -ne $evidence -and $evidence.passed -and $evidence.seconds -le $TimeoutSeconds -and $trial.policies['strict-v1']
    $taskResults | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'trials.json')
    Write-Output ("{0}: pass={1}, kind={2}, seconds={3}" -f (Split-Path -Leaf $directory), $trial.passed, $trial.kind, $trial.seconds)
}
[pscustomobject]@{ trials = $taskResults.Count; passed = @($taskResults | Where-Object passed).Count; workflowTrials = $taskNumber; cap = $Cap; timeoutSeconds = $TimeoutSeconds;
    note = 'Validation checks structure, references, topology, routing and configuration; it does not prove business correctness.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'summary.json')
