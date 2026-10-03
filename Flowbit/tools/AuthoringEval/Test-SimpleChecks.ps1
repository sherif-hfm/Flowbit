param([Parameter(Mandatory=$true)][string]$Runner, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$taskTemplate = '{"kind":"proposal","validation":{"isValid":true,"canSave":true,"canPublish":true},"definition":{"id":"evaluation-existing","name":"Existing approval","initialEventId":1,"flowNodes":[{"id":1,"name":"Start","type":"startEvent"},{"id":2,"name":"Review","type":"userTask","roles":["manager"]},{"id":3,"name":"Approved","type":"endEvent"},{"id":4,"name":"Rejected","type":"endEvent"}],"sequenceFlows":[{"id":1,"sourceRef":1,"targetRef":2},{"id":2,"name":"Approve","sourceRef":2,"targetRef":3},{"id":3,"name":"Reject","sourceRef":2,"targetRef":4}]}}'
$taskCases = @(
    @{ Name='preserved'; Mutate={}; Expected=$true },
    @{ Name='new-label-warning'; Mutate={ $script:model.sequenceFlows[2].name='Rejected' }; Expected=$true },
    @{ Name='old-label-changed'; Mutate={ $script:model.sequenceFlows[1].name='Approved' }; Expected=$false },
    @{ Name='old-position-changed'; Mutate={ $script:model.flowNodes[1] | Add-Member x 100 }; Expected=$false },
    @{ Name='old-roles-changed'; Mutate={ $script:model.flowNodes[1].roles=@('admin') }; Expected=$false },
    @{ Name='old-key-changed'; Mutate={ $script:model.id='different' }; Expected=$false },
    @{ Name='old-flow-id-changed'; Mutate={ $script:model.sequenceFlows[0].id=999 }; Expected=$false },
    @{ Name='wrong-route'; Mutate={ $script:model.sequenceFlows[2].targetRef=3 }; Expected=$false },
    @{ Name='opposite-label'; Mutate={ $script:model.sequenceFlows[2].name='Approve' }; Expected=$false },
    @{ Name='hidden-action'; Mutate={ $script:model.sequenceFlows[2] | Add-Member isSelectable $false }; Expected=$false },
    @{ Name='new-condition'; Mutate={ $script:model.sequenceFlows[2] | Add-Member condition 'amount > 0' }; Expected=$false },
    @{ Name='new-creation-alias'; Fixture='simple'; Mutate={ $script:model.name='Simple approval'; $script:model.sequenceFlows[1].name='Approved' }; Expected=$true }
)
New-Item -ItemType Directory -Path $Output | Out-Null
foreach ($taskCase in $taskCases) {
    $taskResult = $taskTemplate | ConvertFrom-Json
    $script:model = $taskResult.definition
    & $taskCase.Mutate
    $taskDirectory = Join-Path $Output $taskCase.Name
    New-Item -ItemType Directory -Path $taskDirectory | Out-Null
    $taskFile = Join-Path $taskDirectory 'result.json'
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $taskFile
    $taskFixture = if ($taskCase.Fixture) { $taskCase.Fixture } else { 'modify' }
    & dotnet $Runner --check-only $taskFile --output $taskDirectory --fixture $taskFixture --policy functional-v2
    if (($LASTEXITCODE -eq 0) -ne $taskCase.Expected) { throw "Unexpected verdict: $($taskCase.Name)" }
}
Write-Output "$($taskCases.Count) offline creation/preservation checks passed. No key or provider access."
