# Optional LOCAL Assistant preparation. Functions are invoked by the named-profile
# launcher only; dot-sourcing this file does not provision or start anything.
function Resolve-AssistantRuntimeIdentity([bool]$OnWindows, [string]$OperatorUid, [string]$OperatorGid) {
    # Docker Desktop applies the protected host ACL through its file-sharing boundary.
    # Native Linux bind mounts retain the owner's numeric UID and mode 0600.
    if ($OnWindows) { return [pscustomobject]@{ uid='1000'; gid='1000' } }
    foreach ($value in @($OperatorUid, $OperatorGid)) {
        if ($value -notmatch '^[1-9][0-9]{0,9}$' -or [uint64]$value -gt 2147483647) {
            throw 'Assistant requires a supported non-root operator UID and GID. Run as a non-root local operator; secret permissions will not be relaxed.'
        }
    }
    return [pscustomobject]@{ uid=$OperatorUid; gid=$OperatorGid }
}

function Get-AssistantRuntimeIdentity {
    if ($windows) { return Resolve-AssistantRuntimeIdentity $true '' '' }
    $operatorUid = (& id -u)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the Assistant secret owner UID.' }
    $operatorGid = (& id -g)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the Assistant secret owner GID.' }
    return Resolve-AssistantRuntimeIdentity $false $operatorUid.Trim() $operatorGid.Trim()
}

function Get-AssistantExpertIdentity([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { throw 'EnableAssistant requires an explicit SystemExpertRoot.' }
    $resolved = (Resolve-Path -LiteralPath $Root -ErrorAction Stop).Path
    $item = Get-Item -LiteralPath $resolved
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'SystemExpertRoot must be a real source worktree.' }
    $head = (& git -C $resolved rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Cannot establish the Expert candidate identity.' }
    $tree = (& git -C $resolved rev-parse 'HEAD^{tree}')
    if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40}$') { throw 'Cannot establish the Expert candidate tree.' }
    $dirty = (& git -C $resolved status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'The Expert build context must be a clean committed candidate. Preserve changes and commit them before preparation.' }
    $catalogue = Join-Path $resolved 'generated/assistant-runtime/validation-catalogue.json'
    $dockerfile = Join-Path $resolved 'build/assistant/Dockerfile'
    foreach ($required in @($catalogue, $dockerfile)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'Expert candidate is missing its constructed Assistant catalogue or minimal Dockerfile.' }
        if ((Get-Item -LiteralPath $required).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Assistant artifacts cannot be symbolic links.' }
    }
    $hash = (Get-FileHash -LiteralPath $catalogue -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{ root=$resolved; commit=$head.Trim(); tree=$tree.Trim(); catalogue=$catalogue; catalogueSha256=$hash }
}

function Initialize-AssistantProfile([bool]$ExistedBeforeInvocation) {
    $retained = $null -ne $state.PSObject.Properties['assistant']
    if ($DisableAssistant -and -not $retained) { throw 'This profile has no Assistant configuration to disable.' }
    if (-not $EnableAssistant -and -not $retained) { return }
    if (-not $onboarding) { throw 'Assistant requires a named onboarding profile.' }
    if (-not $DisableAssistant -and ($EnableAssistant -or ($retained -and $state.assistant.enabled))) { Get-AssistantRuntimeIdentity | Out-Null }
    if (-not $retained) {
        if ($ExistedBeforeInvocation) { throw 'Assistant is opt-in for a new isolated profile. Existing profiles are not silently upgraded.' }
        $expert = Get-AssistantExpertIdentity $SystemExpertRoot
        $tokens = @{}
        foreach ($purpose in @('knowledgeProduct','knowledgePlatform','diagnosticAnalysis','feedbackIngest','feedbackReview','identityValidation','platformEvaluation','feedbackAudit','amAudit')) { $tokens[$purpose] = Secret }
        $databases = @()
        foreach ($module in @('ai-feedback','am-platform','audit-platform')) {
            $databases += @{ service=$module; name=('uat_' + $Profile.Replace('-','_') + '_' + $module.Replace('-','_')); ownerPassword=(Secret); runtimePassword=(Secret) }
        }
        $state | Add-Member -NotePropertyName assistant -NotePropertyValue ([pscustomobject]@{
            enabled=$true; systemExpertRoot=$expert.root; expertCommit=$expert.commit; expertTree=$expert.tree;
            catalogueSha256=$expert.catalogueSha256; tokens=[pscustomobject]$tokens; databases=$databases
        })
        # This credential must never enter profile.json or a shared runtime bundle.
        Save-Text 'assistant-operator-control' (Secret)
        Save-Json 'profile.json' $state
        Protect-Files
    }
    $assistant = $state.assistant
    if ($assistant.enabled -isnot [bool]) { throw 'Invalid retained Assistant enablement.' }
    if ($DisableAssistant) { $assistant.enabled=$false; Save-Json 'profile.json' $state; Protect-Files; return }
    if ($EnableAssistant -and -not $assistant.enabled) { $assistant.enabled=$true; Save-Json 'profile.json' $state; Protect-Files }
    if (-not $assistant.enabled) { return }
    if ($SystemExpertRoot -and [IO.Path]::GetFullPath($SystemExpertRoot) -ne [IO.Path]::GetFullPath($assistant.systemExpertRoot)) { throw 'The retained Assistant Expert source root cannot be silently replaced.' }
    $expert = Get-AssistantExpertIdentity $assistant.systemExpertRoot
    if ($expert.commit -ne $assistant.expertCommit -or $expert.tree -ne $assistant.expertTree -or $expert.catalogueSha256 -ne $assistant.catalogueSha256) { throw 'Assistant source drift detected. Use an explicitly reviewed new profile; retained credentials and sources are unchanged.' }
    $values = @()
    foreach ($purpose in @('knowledgeProduct','knowledgePlatform','feedbackIngest','feedbackReview','identityValidation','platformEvaluation','feedbackAudit','amAudit')) {
        $value = [string]$assistant.tokens.$purpose
        if ($value -notmatch '^[0-9a-f]{64}$') { throw 'Missing retained Assistant workload credential. Restore the matching protected files; do not regenerate.' }
        $values += $value
    }
    if ($null -ne $assistant.tokens.PSObject.Properties['diagnosticAnalysis']) {
        $diagnosticValue = [string]$assistant.tokens.diagnosticAnalysis
        if ($diagnosticValue -notmatch '^[0-9a-f]{64}$') { throw 'Invalid retained diagnostic workload credential.' }
        $values += $diagnosticValue
    }
    $operatorFile = Join-Path $folder 'assistant-operator-control'
    if (-not (Test-Path -LiteralPath $operatorFile -PathType Leaf)) { throw 'Missing retained Assistant operator credential. Restore its protected file.' }
    $operatorValue = [IO.File]::ReadAllText($operatorFile).Trim()
    if ($operatorValue -notmatch '^[0-9a-f]{64}$') { throw 'Invalid protected Assistant operator credential.' }
    $values += $operatorValue
    $existingValues = @($state.tokens.PSObject.Properties | ForEach-Object { [string]$_.Value })
    if (@($values | Select-Object -Unique).Count -ne $values.Count -or @($values | Where-Object { $existingValues -contains $_ }).Count -ne 0) { throw 'Assistant workload purposes must use distinct credentials.' }
    $expectedModules = @('ai-feedback','am-platform','audit-platform')
    if (@($assistant.databases).Count -ne 3) { throw 'Assistant requires three isolated owner stores.' }
    foreach ($database in $assistant.databases) {
        if ($database.service -notin $expectedModules -or $database.name -ne ('uat_' + $Profile.Replace('-','_') + '_' + $database.service.Replace('-','_')) -or
            $database.ownerPassword -notmatch '^[0-9a-f]{64}$' -or $database.runtimePassword -notmatch '^[0-9a-f]{64}$' -or $database.ownerPassword -eq $database.runtimePassword) { throw 'Invalid retained Assistant database binding.' }
    }
    if (@($assistant.databases.service | Select-Object -Unique).Count -ne 3) { throw 'Assistant owner database bindings must be distinct.' }
    [IO.File]::WriteAllBytes((Join-Path $folder 'assistant-catalogue.json'), [IO.File]::ReadAllBytes($expert.catalogue))
    if ($AssistantPublishedResolutionsFile) {
        $receiptFile = Get-Item -LiteralPath $AssistantPublishedResolutionsFile -ErrorAction Stop
        if ($receiptFile.PSIsContainer -or $receiptFile.Length -gt 65536 -or ($receiptFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid approved publication receipt file.' }
        $receipt = [IO.File]::ReadAllText($receiptFile.FullName) | ConvertFrom-Json
        $catalogue = [IO.File]::ReadAllText($expert.catalogue) | ConvertFrom-Json
        $allowed = @('contractVersion','knowledgeRevision','retrievalRevision','approvedChanges')
        if (@($receipt.PSObject.Properties.Name).Count -ne 4 -or @($receipt.PSObject.Properties.Name | Where-Object { $_ -notin $allowed }).Count -ne 0 -or
            $receipt.contractVersion -ne '1.0.0' -or $receipt.knowledgeRevision -ne $catalogue.knowledgeRevision -or
            $receipt.retrievalRevision -ne $catalogue.retrievalRevision -or $receipt.approvedChanges -isnot [array] -or $receipt.approvedChanges.Count -gt 100) { throw 'Publication receipt does not bind the active reviewed knowledge and retrieval versions.' }
        foreach ($approved in $receipt.approvedChanges) {
            if (@($approved.PSObject.Properties.Name).Count -ne 3 -or @($approved.PSObject.Properties.Name | Where-Object { $_ -notin @('approvedChangeReference','publishedVersion','verificationReference') }).Count -ne 0 -or
                $approved.publishedVersion -notin @($catalogue.knowledgeRevision,$catalogue.retrievalRevision) -or
                $approved.approvedChangeReference -notmatch '^[A-Za-z0-9_.-]{1,256}$' -or $approved.verificationReference -notmatch '^[A-Za-z0-9_.-]{1,256}$') { throw 'Invalid approved publication/verification reference.' }
        }
        $receiptHash = (Get-FileHash -LiteralPath $receiptFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($null -ne $assistant.PSObject.Properties['publishedResolutionsSha256'] -and $receiptHash -ne $assistant.publishedResolutionsSha256) { throw 'Retained published-resolution receipt is immutable. Use a separately reviewed candidate/profile.' }
        [IO.File]::WriteAllBytes((Join-Path $folder 'assistant-published-resolutions.json'), [IO.File]::ReadAllBytes($receiptFile.FullName))
        if ($null -eq $assistant.PSObject.Properties['publishedResolutionsSha256']) {
            $assistant | Add-Member -NotePropertyName publishedResolutionsSha256 -NotePropertyValue $receiptHash
            Save-Json 'profile.json' $state
        }
    }
    if ($null -ne $assistant.PSObject.Properties['publishedResolutionsSha256']) {
        $receiptPath = Join-Path $folder 'assistant-published-resolutions.json'
        if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $assistant.publishedResolutionsSha256) { throw 'Restore the retained approved publication receipt; it cannot be recreated by assertion.' }
    }
    $runtimeConfiguration = @{ productToken=$assistant.tokens.knowledgeProduct; platformToken=$assistant.tokens.knowledgePlatform; catalogueSha256=$assistant.catalogueSha256; port=5122 }
    if ($null -ne $assistant.tokens.PSObject.Properties['diagnosticAnalysis']) { $runtimeConfiguration.diagnosticToken=$assistant.tokens.diagnosticAnalysis }
    Save-Json 'assistant-runtime.json' $runtimeConfiguration
    Protect-Files
}

function Set-AssistantOwnerConfiguration([string]$Service, [hashtable]$Configuration) {
    if ($null -eq $state.PSObject.Properties['assistant'] -or -not $state.assistant.enabled) { return }
    $assistant = $state.assistant
    switch ($Service) {
        'access-management' {
            $database = $assistant.databases | Where-Object service -eq 'am-platform'
            $Configuration.Monergy.AccessManagement.PlatformAuthorization = @{
                Enabled=$true; ConnectionString=(Connection $database 'runtime'); EvaluationToken=$assistant.tokens.platformEvaluation;
                IdentityUrl='http://127.0.0.1:4173/'; IdentityToken=$assistant.tokens.identityValidation;
                AuditUrl='http://127.0.0.1:5102/'; AuditToken=$assistant.tokens.amAudit;
                OperatorCredentialFile='/run/secrets/assistant-operator-control'; CommandUrl='http://127.0.0.1:5088/'
            }
        }
        'ai-intelligence' {
            $database = $assistant.databases | Where-Object service -eq 'ai-feedback'
            $Configuration.Monergy.AssistantFeedback = @{
                Enabled=$true; ConnectionString=(Connection $database 'runtime'); IngestToken=$assistant.tokens.feedbackIngest;
                ReviewToken=$assistant.tokens.feedbackReview; AccessManagementUrl='http://127.0.0.1:5088/';
                PlatformEvaluationToken=$assistant.tokens.platformEvaluation; AuditUrl='http://127.0.0.1:5102/'; AuditToken=$assistant.tokens.feedbackAudit;
                CataloguePath='/run/secrets/assistant-catalogue'; CatalogueSha256=$assistant.catalogueSha256
            }
            if ($null -ne $assistant.PSObject.Properties['publishedResolutionsSha256']) {
                $Configuration.Monergy.AssistantFeedback.PublishedResolutionsPath='/run/secrets/assistant-published-resolutions'
                $Configuration.Monergy.AssistantFeedback.PublishedResolutionsSha256=$assistant.publishedResolutionsSha256
            }
        }
        'audit' {
            $database = $assistant.databases | Where-Object service -eq 'audit-platform'
            $Configuration.Monergy.PlatformAudit = @{
                Enabled=$true; ConnectionString=(Connection $database 'runtime');
                FeedbackToken=$assistant.tokens.feedbackAudit; AmToken=$assistant.tokens.amAudit
            }
        }
        'workspace' {
            $Configuration.Monergy.Assistant = @{
                Enabled=$true; RuntimeUrl='http://127.0.0.1:5122/'; ProductToken=$assistant.tokens.knowledgeProduct;
                PlatformToken=$assistant.tokens.knowledgePlatform; CataloguePath='/run/secrets/assistant-catalogue';
                CatalogueSha256=$assistant.catalogueSha256; FeedbackUrl='http://127.0.0.1:5119/';
                FeedbackIngestToken=$assistant.tokens.feedbackIngest; FeedbackReviewToken=$assistant.tokens.feedbackReview;
                IdentityValidationToken=$assistant.tokens.identityValidation
            }
            if ($null -ne $assistant.tokens.PSObject.Properties['diagnosticAnalysis']) {
                $Configuration.Monergy.Diagnostics = @{
                    RuntimeToken=$assistant.tokens.diagnosticAnalysis
                    SystemExpertUrl='http://127.0.0.1:5122/'
                }
            }
        }
    }
}
