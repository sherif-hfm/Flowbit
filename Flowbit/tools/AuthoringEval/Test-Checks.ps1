param([Parameter(Mandatory=$true)][string]$ExampleResult, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$taskShell = (Get-Process -Id $PID).Path
$taskOriginal = Get-Content -LiteralPath $ExampleResult -Raw
$taskCases = @(
    @{ Name = 'exact'; Mutate = {}; Expected = $true; Warnings = 0 },
    @{ Name = 'alias'; Mutate = { $script:directorFlow.name = 'Director approved' }; Expected = $true; Warnings = 1 },
    @{ Name = 'opposite'; Mutate = { $script:directorFlow.name = 'Reject' }; Expected = $false },
    @{ Name = 'unknown'; Mutate = { $script:directorFlow.name = 'Proceed' }; Expected = $false },
    @{ Name = 'wrong-role-with-alias'; Mutate = { $script:directorFlow.name = 'Director approved'; $script:director.roles = @('requester') }; Expected = $false },
    @{ Name = 'wrong-flow-role-with-alias'; Mutate = { $script:directorFlow.name = 'Director approved'; $script:directorFlow.roles = @('requester') }; Expected = $false },
    @{ Name = 'wrong-route-with-alias'; Mutate = { $script:directorFlow.name = 'Director approved'; $script:directorFlow.targetRef = $script:model.initialEventId }; Expected = $false },
    @{ Name = 'hidden-with-alias'; Mutate = { $script:directorFlow.name = 'Director approved'; $script:directorFlow.isSelectable = $false }; Expected = $false },
    @{ Name = 'condition-with-alias'; Mutate = { $script:directorFlow.name = 'Director approved'; $script:directorFlow | Add-Member condition 'amount > 99999' -Force }; Expected = $false },
    @{ Name = 'quorum-reference'; Mutate = { ($script:model.sequenceFlows | Where-Object { $_.completionPriority -eq 1 }).completionCondition = 'CountFlow(999999) >= 2' }; Expected = $false },
    @{ Name = 'committee-alias'; Mutate = { ($script:model.sequenceFlows | Where-Object { $_.completionPriority -eq 1 }).name = 'Approved' }; Expected = $true; Warnings = 1 },
    @{ Name = 'strict-alias'; Policy = 'strict-v1'; Mutate = { $script:directorFlow.name = 'Director approved' }; Expected = $false }
)
New-Item -ItemType Directory -Path $Output | Out-Null
foreach ($taskCase in $taskCases) {
    $taskResult = $taskOriginal | ConvertFrom-Json
    $script:model = $taskResult.definition
    $script:director = $script:model.flowNodes | Where-Object name -eq 'Director review'
    $script:directorFlow = $script:model.sequenceFlows | Where-Object sourceRef -eq $script:director.id
    $script:directorFlow.name = 'Approve'
    & $taskCase.Mutate
    $taskDirectory = Join-Path $Output $taskCase.Name
    New-Item -ItemType Directory -Path $taskDirectory | Out-Null
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json')
    $taskPolicy = if ($taskCase.Policy) { $taskCase.Policy } else { 'functional-v2' }
    & $taskShell -NoProfile -File "$PSScriptRoot/Check-Complex.ps1" -Directory $taskDirectory -Policy $taskPolicy *> (Join-Path $taskDirectory 'checks.log')
    if (($LASTEXITCODE -eq 0) -ne $taskCase.Expected) { throw "Unexpected verdict: $($taskCase.Name)" }
    $taskAcceptance = Get-Content -LiteralPath (Join-Path $taskDirectory 'acceptance.json') -Raw | ConvertFrom-Json
    if ($taskCase.ContainsKey('Warnings') -and $taskAcceptance.warnings -ne $taskCase.Warnings) { throw "Unexpected warning count: $($taskCase.Name)" }
}
Write-Output "$($taskCases.Count) offline checker regressions passed. No provider calls."
