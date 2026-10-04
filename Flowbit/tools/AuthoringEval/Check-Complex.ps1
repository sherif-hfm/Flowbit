param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [ValidateSet('strict-v1','functional-v2')][string]$Policy = 'strict-v1',
    [string]$OutputDirectory = $Directory
)
$ErrorActionPreference = 'Stop'
$taskResult = Get-Content -LiteralPath (Join-Path $Directory 'result.json') -Raw | ConvertFrom-Json
$taskDefinition = $taskResult.definition
if ($null -eq $taskDefinition) { Write-Output 'No final definition; complex evaluation failed.'; exit 1 }
$taskChecks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$name, [bool]$passed) { $taskChecks.Add([pscustomobject]@{ Name = $name; Passed = $passed; Severity = $(if ($passed) { 'pass' } else { 'error' }) }) }
function Add-Label([string]$name, $flow, [string]$expected, [string[]]$extra = @()) {
    $aliases = @{ Approve = @('Approved'); Reject = @('Rejected'); Complete = @('Completed'); Submit = @('Submitted') }
    $actual = [string]$flow.name
    $exact = $actual -ceq $expected
    $equivalent = $null -ne $flow -and (@($aliases[$expected]) + $extra) -ccontains $actual
    $taskChecks.Add([pscustomobject]@{ Name = $name; Passed = ($exact -or $equivalent);
        Severity = $(if ($exact) { 'pass' } elseif ($equivalent) { 'warning' } else { 'error' }); Expected = $expected; Actual = $actual })
}
function Add-Action([string]$name, [bool]$functional, $flow, [string]$expected, [string[]]$extra = @()) {
    if ($Policy -eq 'strict-v1') { Add-Check $name ($functional -and $flow.name -eq $expected) }
    else { Add-Check ($name + ' (behavior)') $functional; Add-Label ($name + ' (label)') $flow $expected $extra }
}
function Node([string]$name) { @($taskDefinition.flowNodes | Where-Object name -eq $name)[0] }
function Outgoing($node) { ,@($taskDefinition.sequenceFlows | Where-Object sourceRef -eq $node.id) }
function Link($source, $target) { ,@($taskDefinition.sequenceFlows | Where-Object { $_.sourceRef -eq $source.id -and $_.targetRef -eq $target.id }) }
Add-Check 'Validated final proposal' ($taskResult.kind -eq 'proposal' -and $taskResult.validation.isValid -and $taskResult.validation.canSave -and $taskResult.validation.canPublish)
Add-Check 'All 29 nodes, 33 flows and six lanes present' ($taskDefinition.flowNodes.Count -eq 29 -and $taskDefinition.sequenceFlows.Count -eq 33 -and $taskDefinition.lanes.Count -eq 6)
Add-Check 'All six requested lanes' ((@($taskDefinition.lanes.name | Sort-Object) -join ',') -eq 'Committee,Finance,Legal,Operations,Requester,Security')
$taskVars = @{}; foreach ($item in $taskDefinition.variables) { $taskVars[$item.name] = $item }
Add-Check 'Typed amount default' ($taskVars.amount.dataType -eq 'number' -and $taskVars.amount.defaultValue -eq 25000)
Add-Check 'Typed reviewers collection' ($taskVars.reviewers.dataType -eq 'string' -and $taskVars.reviewers.isArray -and ($taskVars.reviewers.defaultValue -join ',') -eq 'alice,bob,carol')
Add-Check 'Votes JSON and approved false defaults' ($taskVars.votes.dataType -eq 'json' -and $taskVars.approved.dataType -eq 'boolean' -and $taskVars.approved.defaultValue -eq $false)
$taskStart = @($taskDefinition.flowNodes | Where-Object type -eq 'startEvent')[0]
$taskIntake = Node 'Requester intake'
$taskFork = @($taskDefinition.flowNodes | Where-Object { $_.type -eq 'parallelGateway' -and (Outgoing $_).Count -eq 3 })[0]
$taskJoin = @($taskDefinition.flowNodes | Where-Object { $_.type -eq 'parallelGateway' -and $_.id -ne $taskFork.id })[0]
Add-Action 'Normal entry to intake then Submit to parallel fork' ($taskDefinition.initialEventId -eq $taskStart.id -and (Link $taskStart $taskIntake).Count -eq 1 -and $taskIntake.roles -contains 'requester' -and (Link $taskIntake $taskFork).Count -eq 1 -and (Link $taskIntake $taskFork)[0].isSelectable -ne $false) (Link $taskIntake $taskFork)[0] 'Submit'
$taskBranches = @(
    @{ Role = 'legal'; Names = @('Contract','Data processing','Export terms','IP rights','Final legal') },
    @{ Role = 'security'; Names = @('Identity','Encryption','Retention','Access','Final security') },
    @{ Role = 'finance'; Names = @('Budget','Tax','Currency','Cost centre','Final finance') }
)
foreach ($branch in $taskBranches) {
    Add-Check ($branch.Role + ' branch begins at fork') ((Link $taskFork (Node $branch.Names[0])).Count -eq 1)
    for ($position = 0; $position -lt $branch.Names.Count; $position++) {
        $taskNode = Node $branch.Names[$position]
        $taskNext = if ($position -lt 4) { Node $branch.Names[$position + 1] } else { $taskJoin }
        $taskFlows = Outgoing $taskNode
        Add-Action ($branch.Names[$position] + ': role, Complete action and next step') ($taskNode.type -eq 'userTask' -and $taskNode.roles -contains $branch.Role -and $taskFlows.Count -eq 1 -and $taskFlows[0].isSelectable -ne $false -and $taskFlows[0].targetRef -eq $taskNext.id) $taskFlows[0] 'Complete'
    }
}
$taskGateway = @($taskDefinition.flowNodes | Where-Object type -eq 'exclusiveGateway')[0]
$taskDirector = Node 'Director review'; $taskCommittee = Node 'Committee vote'; $taskManual = Node 'Manual decision'
Add-Check 'Parallel join precedes amount routing' ((Link $taskJoin $taskGateway).Count -eq 1 -and @($taskDefinition.sequenceFlows | Where-Object targetRef -eq $taskJoin.id).Count -eq 3)
$taskThreshold = (Link $taskGateway $taskDirector)[0]; $taskSkip = (Link $taskGateway $taskCommittee)[0]
Add-Check 'Amount > 10000 priority 1 and default skip' ($taskThreshold.condition -match '^\s*(?:amount|\[amount\])\s*>\s*10000\s*$' -and $taskThreshold.conditionPriority -eq 1 -and $taskSkip.isDefault)
Add-Action 'Director finance Approve enters Committee' ($taskDirector.type -eq 'userTask' -and $taskDirector.roles -contains 'finance' -and (Outgoing $taskDirector).Count -eq 1 -and (Link $taskDirector $taskCommittee).Count -eq 1 -and (Link $taskDirector $taskCommittee)[0].isSelectable -ne $false) (Link $taskDirector $taskCommittee)[0] 'Approve' @('Director approved')
Add-Check 'Parallel collection Committee evaluates afterAll into votes' ($taskCommittee.roles -contains 'committee' -and $taskCommittee.multiInstance.mode -eq 'parallel' -and $taskCommittee.multiInstance.source -eq 'collection' -and $taskCommittee.multiInstance.collectionVariable -eq 'reviewers' -and $taskCommittee.multiInstance.completionEvaluation -eq 'afterAll' -and $taskCommittee.multiInstance.resultVariable -eq 'votes')
$taskVoteFlows = Outgoing $taskCommittee
$taskApprove = @($taskVoteFlows | Where-Object name -eq 'Approve')[0]; $taskReject = @($taskVoteFlows | Where-Object name -eq 'Reject')[0]; $taskFallback = @($taskVoteFlows | Where-Object isDefault -eq $true)[0]
$taskScript = @($taskDefinition.flowNodes | Where-Object type -eq 'scriptTask')[0]; $taskRejected = Node 'Rejected'
if ($Policy -eq 'functional-v2') {
    $taskApprove = @($taskVoteFlows | Where-Object { $_.targetRef -eq $taskScript.id -and !$_.isDefault })[0]
    $taskReject = @($taskVoteFlows | Where-Object { $_.targetRef -eq $taskRejected.id -and !$_.isDefault })[0]
    Add-Label 'Committee approval action label' $taskApprove 'Approve'
    Add-Label 'Committee rejection action label' $taskReject 'Reject'
}
Add-Check 'Two-vote quorum priorities refer to their own flow IDs' ($taskApprove.completionCondition -match ('CountFlow\(' + $taskApprove.id + '\)\s*>=\s*2') -and $taskApprove.completionPriority -eq 1 -and $taskReject.completionCondition -match ('CountFlow\(' + $taskReject.id + '\)\s*>=\s*2') -and $taskReject.completionPriority -eq 2)
Add-Check 'Pure hidden fallback enters Manual decision' ($taskVoteFlows.Count -eq 3 -and $taskFallback.isSelectable -eq $false -and !$taskFallback.condition -and !$taskFallback.completionCondition -and $null -eq $taskFallback.completionPriority -and $taskFallback.targetRef -eq $taskManual.id)
Add-Check 'Selectable votes route approval and normal rejection' ($taskApprove.isSelectable -ne $false -and $taskReject.isSelectable -ne $false -and $taskApprove.targetRef -eq $taskScript.id -and $taskReject.targetRef -eq $taskRejected.id -and $taskRejected.type -eq 'endEvent')
if ($Policy -eq 'strict-v1') {
    Add-Check 'Manual finance decision uses matching Approve and Reject routes' ($taskManual.roles -contains 'finance' -and (Outgoing $taskManual).Count -eq 2 -and (Link $taskManual $taskScript)[0].name -eq 'Approve' -and (Link $taskManual $taskRejected)[0].name -eq 'Reject')
} else {
    Add-Check 'Manual finance decision routes' ($taskManual.type -eq 'userTask' -and $taskManual.roles -contains 'finance' -and (Outgoing $taskManual).Count -eq 2 -and (Link $taskManual $taskScript).Count -eq 1 -and (Link $taskManual $taskRejected).Count -eq 1 -and (Link $taskManual $taskScript)[0].isSelectable -ne $false -and (Link $taskManual $taskRejected)[0].isSelectable -ne $false)
    Add-Label 'Manual approval action label' (Link $taskManual $taskScript)[0] 'Approve'
    Add-Label 'Manual rejection action label' (Link $taskManual $taskRejected)[0] 'Reject'
}
Add-Check 'JavaScript sets approved true' ($taskScript.scriptFormat -eq 'javascript' -and $taskScript.script -match 'execution\.setVariable\(["'']approved["''],\s*true\)')
$taskTimer = @($taskDefinition.flowNodes | Where-Object type -eq 'intermediateTimerCatchEvent')[0]
$taskFulfil = Node 'Fulfil purchase'; $taskClose = Node 'Close purchase'; $taskEnd = Node 'Completed'
Add-Check 'One-hour timer and complete fulfilment tail' ($taskTimer.timer.timeDuration -eq 'PT1H' -and (Link $taskScript $taskTimer).Count -eq 1 -and (Link $taskTimer $taskFulfil).Count -eq 1 -and (Link $taskFulfil $taskClose).Count -eq 1 -and (Link $taskClose $taskEnd).Count -eq 1 -and $taskFulfil.roles -contains 'operations' -and $taskClose.roles -contains 'requester' -and $taskEnd.type -eq 'endEvent')
Add-Check 'Worker prerequisite reported' (($taskResult.message + ' ' + ($taskResult.dependencies -join ' ')) -match 'Worker')
if ($Policy -eq 'functional-v2') {
    # Extra action permissions or predicates must not be hidden by a label warning.
    foreach ($taskNode in @($taskDefinition.flowNodes | Where-Object type -eq 'userTask')) {
        $taskFlows = Outgoing $taskNode
        Add-Check ($taskNode.name + ': authored action permissions and conditions') (!$taskNode.rolesVariable -and $taskNode.roles.Count -eq 1 -and @($taskFlows | Where-Object { $_.rolesVariable -or $_.condition -or @($_.roles | Where-Object { $_ -notin $taskNode.roles }).Count -gt 0 }).Count -eq 0)
    }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$taskChecks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'requirement-checks.json')
$taskChecks | Where-Object Severity -ne 'pass' | Format-Table -AutoSize
Write-Output ("Requirement checks: {0}/{1} passed" -f @($taskChecks | Where-Object Passed).Count, $taskChecks.Count)
[pscustomobject]@{ policy = $Policy; passed = @($taskChecks | Where-Object Passed -eq $false).Count -eq 0; errors = @($taskChecks | Where-Object Severity -eq 'error').Count; warnings = @($taskChecks | Where-Object Severity -eq 'warning').Count } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'acceptance.json')
if (@($taskChecks | Where-Object Passed -eq $false).Count -gt 0) { exit 1 }
