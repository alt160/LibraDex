param(
    [string]$Path = 'artifacts\filter-dx-natural-language-corpus.csv'
)

throw 'Deprecated: do not use this deterministic NL-to-builder generator. The filter DX corpus is now AI-authored from the natural-language rows; use explicit row-reviewed edits only.'

$ErrorActionPreference = 'Stop'

function ConvertTo-CamelName {
    param([string]$Phrase)

    if ($Phrase -match '(?i)\border\s+number\b') { return 'orderNumber' }
    if ($Phrase -match '(?i)\bversion\s+number\b') { return 'versionNumber' }
    if ($Phrase -match '(?i)\bsequence\s+number\b') { return 'sequenceNumber' }

    $clean = $Phrase.ToLowerInvariant()
    $clean = $clean -replace '^\s*(the|a|an)\s+', ''
    $clean = $clean -replace '\b(property|field|value|key|number)\b', ''
    $clean = $clean -replace '\b(binary|nullable|optional|stored|caller-owned|indexed|provided|current|specific|known|supplied)\b', ''
    $clean = $clean -replace '\b(bit|bits)\b', 'bit'
    $clean = $clean -replace '[^a-z0-9]+', ' '
    $parts = @($clean.Trim().Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -eq 0) { return 'value' }

    $first = $parts[0]
    $rest = @()
    if ($parts.Count -gt 1) {
        $rest = foreach ($part in $parts[1..($parts.Count - 1)]) {
            if ($part.Length -gt 0) {
                $part.Substring(0, 1).ToUpperInvariant() + $part.Substring(1)
            }
        }
    }

    return ($first + ($rest -join ''))
}

function ConvertTo-PascalName {
    param([string]$Field)

    if ([string]::IsNullOrWhiteSpace($Field)) { return 'Value' }
    return $Field.Substring(0, 1).ToUpperInvariant() + $Field.Substring(1)
}

function Get-GroupName {
    param([string]$Request)

    if ($Request -match '^Find\s+(.+?)\s+where\s+') {
        $noun = $Matches[1].Trim().ToLowerInvariant()
        if ($noun -match 'composite\s+([a-z-]+)\s+keys') { return ($Matches[1] -replace '-', '') }
        if ($noun -match 'matching\s+rows|matching\s+identities|matching\s+records') { return 'records' }
        if ($noun -match 'records?\s+to\s+(delete|rekey|update)') { return 'records' }
        if ($noun -match 'records?\s+grouped') { return 'records' }
        if ($noun -match '^active users') { return 'users' }
        $first = @($noun.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries))[0]
        if ($first -eq 'people') { return 'people' }
        if ($first.EndsWith('s')) { return $first }
        return $first + 's'
    }

    return 'records'
}

function Get-KeyFamily {
    param([string]$Field, [string]$Criterion)

    $text = ($Field + ' ' + $Criterion).ToLowerInvariant()
    if ($Field -match '(?i)^(status|state|type|category|classification|country|extension)$|(?:Status|State|Type|Category|Classification|Country|Extension)$') { return 'String' }
    if ($Field -match '(?i)(count|amount|price|quantity|percent|percentage|score|priority|shard|retry|sequence|version|code|mask|bucket|rating|size|risk|temperature|pressure|humidity|duration|balance|total|tax|line|attempt|threshold|duplicate|cpu|latitude|longitude)') { return 'Int64' }
    if ($Field -match '(?i)(date|timestamp|expiration|activity|due|created|updated|retention|shipped|delivered|login|changed|effective|closed|retryTime|invoiceDate|orderDate|eventDate|startTime|endTime)') { return 'Date' }
    if ($text -match '\b(file size bytes|size bytes|length prefix)\b') { return 'Int64' }
    if ($text -match '\b(binary|byte array|byte prefix|byte suffix|hash|payload|header|footer|signature|checksum|marker|magic|blob)\b') { return 'Binary' }
    if ($text -match '\bguid\b|\b(tenant id|user id|owner id|folder id|document id|parent document id|customer id|supplier id|principal id|resource id|aggregate id|rule id|device id|correlation id)\b') { return 'Guid' }
    if ($text -match '\b(date|timestamp|expiration|activity|due|created|updated|retention|shipped|delivered|login|failed|changed|effective|closed|retry time|invoice date|order date|event date|start time|end time)\b') { return 'Date' }
    if ($text -match '\b(true|false|flag|enabled|deleted|archived|public|shared|hidden|locked|expired|reviewed|selected|pinned|acknowledged|active|trusted|verified|muted|snoozed|visible|clearance|hazardous|discontinued|required|revoked)\b') { return 'Boolean' }
    if ($text -match '\b(age|count|cents|amount|price|quantity|percent|percentage|score|priority|shard|retry|sequence|version|code|mask|bucket|rating|size|risk|temperature|pressure|humidity|duration|balance|total|tax|line|attempt|threshold|duplicate|cpu|latitude|longitude)\b') { return 'Int64' }
    return 'String'
}

function Get-Selector {
    param([string]$Family)

    switch ($Family) {
        'String' { '.AsString' }
        'Int64' { '.AsInt64' }
        'Guid' { '.AsGuid' }
        'Binary' { '.AsBinary' }
        'Date' { '.AsDateTime' }
        'Boolean' { '.AsBoolean' }
        default { '.AsString' }
    }
}

function ConvertTo-Value {
    param([string]$Value, [string]$Family)

    $trim = $Value.Trim().TrimEnd('.')
    if ($trim -match '^"([^"]*)"$') { return '"' + $Matches[1] + '"' }
    if ($trim -match "^'([^']*)'$") { return "'" + $Matches[1] + "'" }
    if ($trim -match '^(\d+)\s+MB$') { return "$($Matches[1])L * 1024L * 1024L" }
    if ($trim -match '^\d+$') {
        if ($Family -eq 'Int64') { return $trim + 'L' }
        return $trim
    }
    if ($trim -match '^\d+\.\d+$') { return $trim }
    if ($trim -match '^January\s+1\s+2026$') { return 'new DateTime(2026, 1, 1)' }
    if ($trim -match '^the minimum date$') { return 'DateTime.MinValue' }
    if ($trim -match '^a checkpoint instant$') { return 'checkpointInstant' }
    if ($trim -match '^two UTC instants$') { return 'startUtc' }
    if ($trim -match 'guid') { return 'guid' }
    if ($trim -match 'today') { return 'today' }
    if ($trim -match 'tomorrow') { return 'tomorrow' }
    if ($trim -match 'now') { return 'now' }
    if ($trim -match 'threshold') { return 'threshold' }
    if ($trim -match 'cutoff') { return 'cutoff' }
    if ($trim -match 'checkpoint') { return 'checkpoint' }
    if ($trim -match 'current tenant') { return 'currentTenantId' }
    if ($trim -match 'current user') { return 'currentUserId' }

    $token = ConvertTo-CamelName $trim
    if ([string]::IsNullOrWhiteSpace($token)) { return 'value' }
    if ($Family -eq 'String' -and $trim -match '^[A-Z][A-Za-z0-9_-]*$') { return '"' + $trim + '"' }
    return $token
}

function New-Predicate {
    param(
        [string]$Field,
        [string]$Family,
        [string]$Operation,
        [string]$Argument,
        [string]$SecondArgument = ''
    )

    $selector = Get-Selector $Family
    switch ($Operation) {
        'eq' { return ".Index(`"$Field`")$selector.EqualTo($Argument)" }
        'neq' { return ".Index(`"$Field`")$selector.NotEqualTo($Argument)" }
        'gt' { return ".Index(`"$Field`")$selector.GreaterThan($Argument)" }
        'gte' { return ".Index(`"$Field`")$selector.GreaterOrEqual($Argument)" }
        'lt' { return ".Index(`"$Field`")$selector.LessThan($Argument)" }
        'lte' { return ".Index(`"$Field`")$selector.LessOrEqual($Argument)" }
        'between' { return ".Index(`"$Field`")$selector.Between($Argument, $SecondArgument)" }
        'notbetween' { return ".Not.Index(`"$Field`")$selector.Between($Argument, $SecondArgument)" }
        'starts' { return ".Index(`"$Field`")$selector.StartsWith($Argument)" }
        'ends' { return ".Index(`"$Field`")$selector.EndsWith($Argument)" }
        'contains' { return ".Index(`"$Field`")$selector.Contains($Argument)" }
        'notcontains' { return ".Not.Index(`"$Field`")$selector.Contains($Argument)" }
        'matches' { return ".Index(`"$Field`")$selector.Matches($Argument)" }
        'in' { return ".Index(`"$Field`")$selector.InSet($Argument)" }
        'notin' { return ".Index(`"$Field`")$selector.NotInSet($Argument)" }
        'null' {
            if ($Family -in @('String', 'Binary')) { return ".Index(`"$Field`")$selector.EqualTo(NullKey.Null)" }
            return ".Index(`"$Field`")$selector.EqualTo(ScalarNull.Null)"
        }
        'notnull' {
            if ($Family -in @('String', 'Binary')) { return ".Index(`"$Field`")$selector.NotEqualTo(NullKey.Null)" }
            return ".Index(`"$Field`")$selector.EqualTo(ScalarNull.NonNull)"
        }
        'empty' {
            if ($Family -in @('String', 'Binary')) { return ".Index(`"$Field`")$selector.EqualTo(NullKey.Empty)" }
            return ".Index(`"$Field`")$selector.EqualTo(emptyValue)"
        }
        'nullorempty' {
            if ($Family -in @('String', 'Binary')) { return ".Index(`"$Field`")$selector.EqualTo(NullKey.NullOrEmpty)" }
            return ".Index(`"$Field`")$selector.EqualTo(ScalarNull.Null)"
        }
        'bitset' { return ".Index(`"$Field`")$selector.BitAnd($Argument, $Argument)" }
        'bitclear' { return ".Index(`"$Field`")$selector.BitAnd($Argument, 0L)" }
        'allbits' { return ".Index(`"$Field`")$selector.AllBitsSet($Argument)" }
        'nobits' { return ".Index(`"$Field`")$selector.NoBitsSet($Argument)" }
        'weekend' { return ".Index(`"$Field`")$selector.IsWeekend()" }
        'weekday' { return ".Not.Index(`"$Field`")$selector.IsWeekend()" }
        'morning' { return ".Index(`"$Field`")$selector.IsMorning()" }
        'afternoon' { return ".Index(`"$Field`")$selector.IsAfternoon()" }
        'evening' { return ".Index(`"$Field`")$selector.IsEvening()" }
        'night' { return ".Index(`"$Field`")$selector.IsNight()" }
        'year' { return ".Index(`"$Field`")$selector.YearEqualTo($Argument)" }
        'month' { return ".Index(`"$Field`")$selector.MonthEqualTo($Argument)" }
        'dayrange' { return ".Index(`"$Field`")$selector.DayRange($Argument, $SecondArgument)" }
        'yearmonth' { return ".Index(`"$Field`")$selector.YearMonth($Argument, $SecondArgument)" }
        'quarter' { return ".Index(`"$Field`")$selector.YearQuarter($Argument, $SecondArgument)" }
        'inquarter' { return ".Index(`"$Field`")$selector.InQuarter($Argument)" }
        'lastofmonth' { return ".Index(`"$Field`")$selector.IsLastOfMonth()" }
    }
}

function Convert-PredicateToCatalog {
    param([string]$Predicate, [bool]$First, [string]$Connector = 'AND')

    $text = $Predicate
    if ($First) {
        $text = $text -replace '^\.Not\.Index\(', '.Not.Index('
        return ($text -replace '^\.Index\(', '.Where(')
    }

    if ($Connector -eq 'OR') {
        $text = $text -replace '^\.Not\.Index\(', '.Or.Not.Index('
        return ($text -replace '^\.Index\(', '.OrElse(')
    }

    $text = $text -replace '^\.Not\.Index\(', '.And.Not.Index('
    return ($text -replace '^\.Index\(', '.AndAlso(')
}

function Convert-PredicateToAbraxas {
    param([string]$Predicate)

    $text = $Predicate
    $text = [regex]::Replace($text, '\.Index\("([^"]+)"\)', {
        param($m)
        '.PropPath(".' + (ConvertTo-PascalName $m.Groups[1].Value) + '")'
    })
    $text = $text -replace '\.Not\.PropPath', '.Not.PropPath'
    $text = $text -replace 'NullKey\.NullOrEmpty', 'nullOrEmpty'
    $text = $text -replace 'NullKey\.Null', 'null'
    $text = $text -replace 'NullKey\.Empty', 'string.Empty'
    $text = $text -replace 'ScalarNull\.Null', 'null'
    $text = $text -replace 'ScalarNull\.NonNull', 'notNull'
    return $text
}

function Complete-Condition {
    param([string]$Group, [array]$Predicates, [array]$Connectors)

    if ($Predicates.Count -eq 0) { return $null }
    $raw = "LibraDexCondition.ForGroup(`"$Group`")" + $Predicates[0]
    $cat = "catalog[`"$Group`"]" + (Convert-PredicateToCatalog $Predicates[0] $true)
    $abr = 'store.Where' + (Convert-PredicateToAbraxas $Predicates[0])

    for ($i = 1; $i -lt $Predicates.Count; $i++) {
        $connector = if ($Connectors[$i - 1] -eq 'OR') { 'OR' } else { 'AND' }
        $raw += ".$connector" + $Predicates[$i]
        $cat += (Convert-PredicateToCatalog $Predicates[$i] $false $connector)
        $abr += ".$connector" + (Convert-PredicateToAbraxas $Predicates[$i])
    }

    [pscustomobject]@{
        LibraDex = "ForGroup: $raw.EndCondition | catalog group: $cat.EndCondition"
        Abraxas = "$abr.EndCondition"
    }
}

function New-Unsupported {
    param([string]$Reason)

    [pscustomobject]@{
        LibraDex = ''
        Abraxas = ''
        Score = '0'
        Notes = $Reason
    }
}

function New-Supported {
    param([string]$Group, [array]$Predicates, [array]$Connectors, [string]$Notes = '')

    $condition = Complete-Condition $Group $Predicates $Connectors
    [pscustomobject]@{
        LibraDex = $condition.LibraDex
        Abraxas = $condition.Abraxas
        Score = '5'
        Notes = if ($Notes) { $Notes } else { 'Direct indexed condition syntax matches every predicate in the natural-language request.' }
    }
}

function New-ManualSyntax {
    param(
        [string]$LibraDex,
        [string]$Abraxas,
        [string]$Notes
    )

    [pscustomobject]@{
        LibraDex = $LibraDex
        Abraxas = $Abraxas
        Score = '5'
        Notes = $Notes
    }
}

function Test-IdIn {
    param([string]$Id, [string[]]$Ids)

    return $Ids -contains $Id
}

function Get-CapabilityInventoryNote {
    param([string]$Id)

    $direct = @(
        'F212', 'F275', 'F277', 'F280', 'F434', 'F435', 'F436', 'F437', 'F438', 'F439', 'F440',
        'F451', 'F452', 'F453', 'F454', 'F456', 'F512', 'F513', 'F516', 'F519', 'F520', 'F522',
        'F531', 'F533', 'F534', 'F543', 'F548', 'F582', 'F583', 'F588', 'F608', 'F621', 'F627'
    )
    $composition = @(
        'F373', 'F374', 'F381', 'F382', 'F383', 'F384', 'F386', 'F387', 'F389', 'F390', 'F391',
        'F394', 'F396', 'F397', 'F401', 'F403', 'F405', 'F408', 'F411', 'F412', 'F413', 'F415',
        'F417', 'F418', 'F420', 'F422', 'F423', 'F424', 'F425', 'F426', 'F427', 'F428', 'F429',
        'F430', 'F441', 'F442', 'F443', 'F450', 'F455', 'F457', 'F458', 'F459', 'F460', 'F461',
        'F477', 'F478', 'F479', 'F480', 'F503', 'F505', 'F508', 'F517', 'F518', 'F537', 'F538',
        'F539', 'F540', 'F546', 'F549', 'F561', 'F566', 'F568', 'F587', 'F611', 'F612', 'F613',
        'F614', 'F615', 'F616', 'F617', 'F619', 'F635', 'F637', 'F638'
    )
    $projection = @(
        'F098', 'F147', 'F159', 'F206', 'F225', 'F227', 'F228', 'F229', 'F244', 'F249', 'F270',
        'F271', 'F272', 'F306', 'F447', 'F487', 'F490', 'F493', 'F494', 'F511', 'F526', 'F527',
        'F528', 'F529', 'F530', 'F532', 'F535', 'F536', 'F547', 'F550', 'F551', 'F552', 'F554',
        'F555', 'F556', 'F557', 'F558', 'F559', 'F560', 'F562', 'F563', 'F564', 'F565', 'F569', 'F570',
        'F572', 'F573', 'F574', 'F575', 'F576', 'F580', 'F584', 'F585', 'F586', 'F589', 'F590',
        'F591', 'F592', 'F593', 'F594', 'F595', 'F597', 'F598', 'F599', 'F600', 'F601', 'F602', 'F603',
        'F604', 'F607', 'F609', 'F610', 'F630', 'F631', 'F632', 'F636', 'F640', 'F641'
    )
    $notConditionScope = @(
        'F462', 'F463', 'F464', 'F465', 'F466', 'F467', 'F468', 'F469', 'F470', 'F471',
        'F472', 'F473', 'F474', 'F475', 'F476'
    )
    $terminalWrapper = @('F484', 'F485', 'F486', 'F488', 'F489', 'F495')
    $builderGap = @('F053', 'F060', 'F063', 'F070', 'F509', 'F510')
    $needsDecision = @('F506', 'F507')

    if (Test-IdIn $Id $direct) {
        return 'Capability inventory: expressible with current fluent condition syntax; generator still needs a row-specific mapping before this should score 5.'
    }
    if (Test-IdIn $Id $composition) {
        return 'Capability inventory: expressible through current composition syntax such as Group(...), KeyPart(...), FullKey(...), MultiKey(...), .Not, or .External(...); generator needs a non-flat mapping before this should score 5.'
    }
    if (Test-IdIn $Id $projection) {
        return 'Capability inventory: not a condition-builder API gap; this needs a maintained projection, modeled index key, caller-owned External(...) data, or an explicit semantic rewrite.'
    }
    if (Test-IdIn $Id $notConditionScope) {
        return 'Capability inventory: outside condition-builder scope; this is retrieval, result-shape, ordering, paging, cursor, or aggregation behavior.'
    }
    if (Test-IdIn $Id $terminalWrapper) {
        return 'Capability inventory: predicate is expressible, but the row asks about a terminal delete, rekey, or update wrapper rather than condition syntax itself.'
    }
    if (Test-IdIn $Id $builderGap) {
        return 'Capability inventory: likely requires new builder API if direct fluent syntax is desired, especially regex-existence, string normalization, or whitespace-policy predicates.'
    }
    if (Test-IdIn $Id $needsDecision) {
        return 'Capability inventory: needs an explicit policy decision before scoring because the row describes missing-value ordering or range-participation semantics rather than syntax.'
    }

    return ''
}

function Split-Criteria {
    param([string]$Text)

    $criteria = $Text.Trim().TrimEnd('.')
    if ($criteria -match '\(.+\)') { return $null }

    $protected = $criteria
    $protected = $protected -replace '(?i)greater than or equal to', 'greater than __LOCAL_OR__ equal to'
    $protected = $protected -replace '(?i)less than or equal to', 'less than __LOCAL_OR__ equal to'
    $protected = $protected -replace '(?i)on or after', 'on __LOCAL_OR__ after'
    $protected = $protected -replace '(?i)at or after', 'at __LOCAL_OR__ after'
    $protected = [regex]::Replace($protected, '(?i)(between\s+.+?)\s+and\s+(.+?)(?=\s+and\s+|\s+or\s+|$)', {
        param($m)
        $m.Groups[1].Value + ' __BETWEEN_AND__ ' + $m.Groups[2].Value
    })
    $protected = $protected -replace ',\s+or\s+', ', __LIST_OR__ '
    $protected = [regex]::Replace($protected, '(?i)(one of\s+.+?)\s+or\s+(.+?)(?=\s+and\s+|\s+or\s+|$)', {
        param($m)
        $m.Groups[1].Value + ' __LIST_OR__ ' + $m.Groups[2].Value
    })

    if ($protected -match '\s+or\s+') {
        $parts = @($protected -split '\s+or\s+') | ForEach-Object { $_ -replace '__BETWEEN_AND__', 'and' -replace '__LIST_OR__', 'or' -replace '__LOCAL_OR__', 'or' }
        return [pscustomobject]@{ Parts = $parts; Connectors = @('OR') * ($parts.Count - 1) }
    }
    if ($protected -match '\s+and\s+') {
        $parts = @($protected -split '\s+and\s+') | ForEach-Object { $_ -replace '__BETWEEN_AND__', 'and' -replace '__LIST_OR__', 'or' -replace '__LOCAL_OR__', 'or' }
        return [pscustomobject]@{ Parts = $parts; Connectors = @('AND') * ($parts.Count - 1) }
    }
    [pscustomobject]@{ Parts = @($criteria); Connectors = @() }
}

function Parse-Part {
    param([string]$Part)

    $p = $Part.Trim().TrimEnd('.')
    $p = $p -replace '^where\s+', ''

    if ($p -match '(.+?)\s+is (missing|null)$' -or $p -match '(.+?)\s+has no value$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'null' ''
    }
    if ($p -match '(.+?)\s+bytes are missing$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'null' ''
    }
    if ($p -match '(.+?)\s+is nonzero$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'neq' '0L'
    }
    if ($p -match '(.+?)\s+is present$' -or $p -match '(.+?)\s+has a real value$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'notnull' ''
    }
    if ($p -match '(.+?)\s+is blank$' -or $p -match '(.+?)\s+is exactly the empty string$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'String' 'empty' ''
    }
    if ($p -match '(.+?)\s+is not blank$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'String' 'notnull' ''
    }
    if ($p -match '(.+?)\s+starts with\s+(".*?"|[A-Za-z0-9_.-]+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        if ($family -notin @('String', 'Binary', 'Guid')) { $family = 'String' }
        return New-Predicate $field $family 'starts' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+(starts with|begins with)\s+a (known )?byte prefix$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'starts' 'prefixBytes'
    }
    if ($p -match '(.+?)\s+(starts with|begins with)\s+a marker$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'starts' 'markerBytes'
    }
    if ($p -match '(.+?)\s+(starts with|begins with)\s+a known GUID prefix$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'starts' 'guidPrefix'
    }
    if ($p -match '(.+?)\s+begins with\s+a magic number$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'starts' 'magicBytes'
    }
    if ($p -match '(.+?)\s+ends with\s+(".*?"|[A-Za-z0-9_.@-]+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        if ($family -notin @('String', 'Binary', 'Guid')) { $family = 'String' }
        return New-Predicate $field $family 'ends' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+ends with\s+a known byte suffix$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'ends' 'suffixBytes'
    }
    if ($p -match '(.+?)\s+suffix is a known signature$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'ends' 'signatureBytes'
    }
    if ($p -match '(.+?)\s+does not contain\s+(".*?"|[A-Za-z0-9_.@-]+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        if ($family -notin @('String', 'Binary', 'Guid')) { $family = 'String' }
        return New-Predicate $field $family 'notcontains' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+does not start with\s+(".*?"|[A-Za-z0-9_.@-]+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Not.Index(`"$field`").AsString.StartsWith($($Matches[2]))"
    }
    if ($p -match '(.+?)\s+does not begin with\s+the expected magic number$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Not.Index(`"$field`").AsBinary.StartsWith(expectedMagicBytes)"
    }
    if ($p -match '(.+?)\s+contains\s+both\s+(".*?")\s+and\s+(".*?")$') {
        return $null
    }
    if ($p -match '(.+?)\s+contains\s+either\s+(".*?")\s+or\s+(".*?")$') {
        return $null
    }
    if ($p -match '(.+?)\s+contains\s+(".*?"|[A-Za-z0-9_.+@-]+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        if ($family -notin @('String', 'Binary', 'Guid')) { $family = 'String' }
        return New-Predicate $field $family 'contains' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+contains\s+a known GUID segment$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'contains' 'guidSegment'
    }
    if ($p -match '(.+?)\s+contains\s+the byte sequence 00 FF$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'contains' 'new byte[] { 0x00, 0xFF }'
    }
    if ($p -match '(.+?)\s+contains\s+a null byte$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'contains' 'new byte[] { 0x00 }'
    }
    if ($p -match '(.+?)\s+contains\s+a protocol marker$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'contains' 'protocolMarker'
    }
    if ($p -match '(.+?)\s+contains\s+a UTF-8 field name$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'contains' 'utf8FieldNameBytes'
    }
    if ($p -match '(.+?)\s+matches\s+(".*?"|\*.*)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        if ($family -notin @('String', 'Binary', 'Guid')) { $family = 'String' }
        return New-Predicate $field $family 'matches' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+matches the wildcard pattern\s+(".*?")$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'String' 'matches' $Matches[2]
    }
    if ($p -match '(.+?)\s+matches\s+a wildcard GUID string$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'matches' 'guidPattern'
    }
    if ($p -match '(.+?)\s+is case-insensitively equal to\s+(".*?")$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Index(`"$field`").AsString.EqualTo($($Matches[2]), ignoreCase: true)"
    }
    if ($p -match '(.+?)\s+starts with\s+(".*?")\s+regardless of case$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Index(`"$field`").AsString.StartsWith($($Matches[2]), ignoreCase: true)"
    }
    if ($p -match '(.+?)\s+contains\s+(".*?")\s+regardless of case$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Index(`"$field`").AsString.Contains($($Matches[2]), ignoreCase: true)"
    }
    if ($p -match '(.+?)\s+is in June 2026$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'yearmonth' '2026' '6'
    }
    if ($p -match '(.+?)\s+is in Q([1-4]) of 2026$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'quarter' '2026' $Matches[2]
    }
    if ($p -match '(.+?)\s+is in Q([1-4])$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'inquarter' $Matches[2]
    }
    if ($p -match '(.+?)\s+is not in\s+(.+)$' -or $p -match '(.+?)\s+is not one of\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'notin' 'values'
    }
    if ($p -match '(.+?)\s+is in\s+(.+)$' -or $p -match '(.+?)\s+is one of\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'in' 'values'
    }
    if ($p -match '(.+?)\s+is outside\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'notbetween' 'min' 'max'
    }
    if ($p -match '(.+?)\s+is alphabetically between\s+(".*?")\s+and\s+(".*?")$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'String' 'between' $Matches[2] $Matches[3]
    }
    if ($p -match '(.+?)\s+is between\s+(.+?)\s+and\s+(.+)$' -or $p -match '(.+?)\s+is between\s+(.+?)\s+through\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'between' (ConvertTo-Value $Matches[2] $family) (ConvertTo-Value $Matches[3] $family)
    }
    if ($p -match '(.+?)\s+is not between two supplied dates$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'notbetween' 'startDate' 'endDate'
    }
    if ($p -match '(.+?)\s+is between two supplied dates$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'startDate' 'endDate'
    }
    if ($p -match '(.+?)\s+is between two UTC instants$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'startUtc' 'endUtc'
    }
    if ($p -match '(.+?)\s+is between two GUID byte-order boundaries$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'between' 'minGuid' 'maxGuid'
    }
    if ($p -match '(.+?)\s+is greater than or equal to\s+(.+)$' -or $p -match '(.+?)\s+is at least\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'gte' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+is higher than\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'gt' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+(is greater than|is above|exceeds|is over)\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'gt' (ConvertTo-Value $Matches[3] $family)
    }
    if ($p -match '(.+?)\s+(is less than|is below|is under|sorts before|is before|is older than)\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'lt' (ConvertTo-Value $Matches[3] $family)
    }
    if ($p -match '(.+?)\s+sorts after\s+(.+)$' -or $p -match '(.+?)\s+is after\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'gt' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+is on or after\s+(.+)$' -or $p -match '(.+?)\s+is at or after\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'gte' (ConvertTo-Value $Matches[2] 'Date')
    }
    if ($p -match '(.+?)\s+is not\s+(".*?"|[A-Z][A-Za-z0-9_-]*)$' -or $p -match '(.+?)\s+is different from\s+(".*?"|[A-Z][A-Za-z0-9_-]*)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'neq' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+is not\s+a specific GUID$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'neq' 'guid'
    }
    if ($p -match '(.+?)\s+(equals|equal to|is)\s+a GUID$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'eq' 'guid'
    }
    if ($p -match '(.+?)\s+is not Guid.Empty$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'neq' 'Guid.Empty'
    }
    if ($p -match '(.+?)\s+(equals|is|equal to)\s+(".*?"|''.*?''|[A-Za-z0-9_.@-]+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'eq' (ConvertTo-Value $Matches[3] $family)
    }
    if ($p -match '(.+?)\s+equals\s+a specific GUID$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'eq' 'guid'
    }
    if ($p -match '(.+?)\s+equals\s+a provided byte array$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'eq' 'bytes'
    }
    if ($p -match '(.+?)\s+equals Guid.Empty$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'eq' 'Guid.Empty'
    }
    if ($p -match '(.+?)\s+is exactly\s+(.+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'eq' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+is on a weekend$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'weekend' ''
    }
    if ($p -match '(.+?)\s+is on a weekday$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'weekday' ''
    }
    if ($p -match '(.+?)\s+is in the morning$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'morning' ''
    }
    if ($p -match '(.+?)\s+is in the afternoon$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'afternoon' ''
    }
    if ($p -match '(.+?)\s+is in the evening$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'evening' ''
    }
    if ($p -match '(.+?)\s+is at night$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'night' ''
    }
    if ($p -match '(.+?)\s+is overdue$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'lt' 'now'
    }
    if ($p -match '(.+?)\s+is today$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'todayStart' 'todayEnd'
    }
    if ($p -match '(.+?)\s+equals a specific calendar day regardless of time$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'dayStart' 'dayEnd'
    }
    if ($p -match '(.+?)\s+is this month$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'monthStart' 'monthEnd'
    }
    if ($p -match '(.+?)\s+is this quarter$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'quarterStart' 'quarterEnd'
    }
    if ($p -match '(.+?)\s+is this year$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'yearStart' 'yearEnd'
    }
    if ($p -match '(.+?)\s+is last month$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'lastMonthStart' 'lastMonthEnd'
    }
    if ($p -match '(.+?)\s+is tomorrow$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'tomorrowStart' 'tomorrowEnd'
    }
    if ($p -match '(.+?)\s+is (in|within) the last\s+(\d+)\s+(days|hours|minutes)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' "now.Add$($Matches[4].Substring(0,1).ToUpperInvariant() + $Matches[4].Substring(1))(-$($Matches[3]))" 'now'
    }
    if ($p -match '(.+?)\s+is within the last hour$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'now.AddHours(-1)' 'now'
    }
    if ($p -match '(.+?)\s+is in the next\s+(\d+)\s+days$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'today' "today.AddDays($($Matches[2]))"
    }
    if ($p -match '(.+?)\s+is in June 2026$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'yearmonth' '2026' '6'
    }
    if ($p -match '(.+?)\s+is in Q([1-4]) of 2026$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'quarter' '2026' $Matches[2]
    }
    if ($p -match '(.+?)\s+is in Q([1-4])$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'inquarter' $Matches[2]
    }
    if ($p -match '(.+?)\s+has day-of-month equal to\s+(\d+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Index(`"$field`").AsDateTime.DayEqualTo($($Matches[2]))"
    }
    if ($p -match '(.+?)\s+has day-of-month between\s+(\d+)\s+and\s+(\d+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'dayrange' $Matches[2] $Matches[3]
    }
    if ($p -match '(.+?)\s+is the same month every year$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'month' 'month'
    }
    if ($p -match '(.+?)\s+is a leap day$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Index(`"$field`").AsDateTime.MonthDay(2, 29)"
    }
    if ($p -match '(.+?)\s+is the last day of the month$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'lastofmonth' ''
    }
    if ($p -match '(.+?)\s+has month equal to December$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'month' '12'
    }
    if ($p -match '(.+?)\s+is true$' -or $p -match '(.+?)\s+is set$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Boolean' 'eq' 'true'
    }
    if ($p -match '(.+?)\s+is false$' -or $p -match '(.+?)\s+is not set$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Boolean' 'eq' 'false'
    }
    if ($p -match '(.+?)\s+has read set$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Int64' 'allbits' 'readMask'
    }
    if ($p -match '(.+?)\s+has none of the reserved (header )?bits set$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Int64' 'nobits' 'reservedMask'
    }
    if ($p -match '(.+?)\s+contains all required flags$' -or $p -match '(.+?)\s+has all required bits$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Int64' 'allbits' 'requiredMask'
    }

    return $null
}

function Complete-General {
    param([string]$Request, [string]$ExistingScore = '')

    $group = Get-GroupName $Request
    if ($Request -notmatch '^Find\s+.+?\s+where\s+(.+)$') {
        return New-Unsupported 'The request is not a condition-builder predicate; it describes result shaping, grouping, paging, mutation, or another terminal operation.'
    }

    $criteria = $Matches[1].Trim()
    if ($ExistingScore -eq '0' -and $Request -match '\bcomposite\b|\broute key\b') {
        return New-Unsupported 'Score 0: composite and route-key rows require explicit KeyPart(...), FullKey(...), Parts(...), Excluding(...), or ordered MultiKey(...) mapping; the general parser must not flatten them into ordinary index leaves.'
    }

    $unsupportedPatterns = @(
        'before .+ and .+ before',
        'greater than .+ count',
        'greater than .+ total',
        'greater than .+ amount',
        'equals the stored checksum',
        'differs from',
        'changed from',
        'current row',
        'calculated',
        'computed',
        'percentile',
        'stored elsewhere',
        'business day',
        'fiscal',
        'ISO week',
        'daylight-saving',
        'tenant local time zone',
        'overlaps',
        'same customer',
        'same tenant',
        'no explicit ACL',
        'multiple permission rows',
        'team ACL',
        'unique within',
        'sort-last',
        'during business hours',
        'outside business hours',
        'crosses midnight',
        'wraps across midnight',
        'normalization changed',
        'projection is stale',
        'projection is missing',
        'after trimming',
        'folding accents',
        'collapses whitespace',
        'whitespace-only',
        'all considered blank',
        'after currency conversion',
        'compatible with',
        'prerelease',
        'semantic version',
        'IP address is in',
        'outside a denylisted range',
        'route key equals tenant id plus',
        'route key selects',
        'generated code chooses',
        'selected index name is determined',
        'caller-supplied business-hours',
        'requested appointment time',
        'binary slice',
        'interpreted as',
        'stored in a binary slice',
        '^accessible means',
        '^.+ means .+ either',
        'ACL role'
    )

    foreach ($pattern in $unsupportedPatterns) {
        if ($criteria -match $pattern) {
            return New-Unsupported "Score 0: the request needs a cross-field comparison, derived/domain computation, dynamic planning rule, or caller-owned semantic policy ('$pattern') that cannot be faithfully represented as ordinary indexed C# condition syntax without an explicit projection or external data source."
        }
    }

    $split = Split-Criteria $criteria
    if ($null -eq $split) {
        return New-Unsupported 'Score 0: nested grouping is present, but this conservative rebuild could not parse every grouped predicate exactly from the natural-language row.'
    }

    $predicates = @()
    foreach ($part in $split.Parts) {
        $predicate = Parse-Part $part
        if ($null -eq $predicate) {
            return New-Unsupported "Score 0: no faithful C# condition syntax was generated for predicate segment '$part'."
        }

        $predicates += $predicate
    }

    return New-Supported $group $predicates $split.Connectors
}

function Complete-Manual {
    param([string]$Id)

    switch ($Id) {
        'F037' { return New-Supported 'records' @((New-Predicate 'title' 'String' 'contains' '"urgent"'), (New-Predicate 'title' 'String' 'contains' '"review"')) @('AND') }
        'F038' { return New-Supported 'records' @((New-Predicate 'title' 'String' 'contains' '"urgent"'), (New-Predicate 'title' 'String' 'contains' '"escalated"')) @('OR') }
        'F015' { return New-Supported 'customers' @((New-Predicate 'lastName' 'String' 'contains' '"''"')) @() }
        'F027' { return New-Supported 'users' @((New-Predicate 'emailAddress' 'String' 'notnull' ''), (New-Predicate 'emailVerified' 'Boolean' 'eq' 'false')) @('AND') }
        'F030' { return New-Supported 'users' @((New-Predicate 'username' 'String' 'contains' '"-"')) @() }
        'F044' { return New-Supported 'orders' @((New-Predicate 'orderNumber' 'String' 'contains' '"PHX"')) @() }
        'F057' { return New-Supported 'tickets' @((New-Predicate 'subject' 'String' 'contains' '"locked out"'), (New-Predicate 'subject' 'String' 'contains' '"login failed"')) @('OR') }
        'F067' { return New-Supported 'logs' @((New-Predicate 'message' 'String' 'contains' '"timeout"'), (New-Predicate 'message' 'String' 'notcontains' '"retry succeeded"')) @('AND') }
        'F125' { return New-Supported 'events' @((New-Predicate 'versionNumber' 'Int64' 'bitset' 'versionBitMask')) @() }
        'F135' { return New-Supported 'records' @((New-Predicate 'public' 'Boolean' 'eq' 'true'), (New-Predicate 'shared' 'Boolean' 'eq' 'true')) @('OR') }
        'F140' { return New-Supported 'records' @((New-Predicate 'approvalA' 'Boolean' 'eq' 'true'), (New-Predicate 'approvalB' 'Boolean' 'eq' 'true'), (New-Predicate 'approvalC' 'Boolean' 'eq' 'true')) @('AND', 'AND') 'The natural language names a fixed three-approval Boolean model; the condition keeps the three stored Boolean leaves explicit.' }
        'F144' { return New-Supported 'tasks' @((New-Predicate 'status' 'String' 'notin' 'new[] { "Closed", "Cancelled" }')) @() }
        'F150' { return New-Supported 'tasks' @((New-Predicate 'workflowState' 'String' 'null' ''), (New-Predicate 'workflowState' 'String' 'eq' '"Unknown"')) @('OR') }
        'F153' { return New-Supported 'permissions' @((New-Predicate 'permissionMask' 'Int64' 'allbits' '(readMask | writeMask)')) @() 'The request is a stored bitmask predicate; `.AllBitsSet(...)` expresses both required bits without scanning caller policy.' }
        'F158' { return New-Supported 'permissions' @((New-Predicate 'mask' 'Int64' 'allbits' 'requiredMask'), (New-Predicate 'mask' 'Int64' 'nobits' 'forbiddenMask')) @('AND') 'The request maps to one required-bits predicate and one forbidden-bits predicate over the same stored mask.' }
        'F162' { return New-Supported 'flags' @((New-Predicate 'beta' 'Boolean' 'eq' 'true'), (New-Predicate 'preview' 'Boolean' 'eq' 'true')) @('OR') 'The local either/or over two stored Boolean flags is ordinary OR composition.' }
        'F165' { return New-Supported 'flags' @((New-Predicate 'rolloutMask' 'Int64' 'nobits' 'rolloutMask')) @() 'The request is a stored bitmask predicate where none of the rollout bits may be set.' }
        'F187' { return New-Supported 'rows' @((New-Predicate 'updatedTimestamp' 'Date' 'null' ''), (New-Predicate 'updatedTimestamp' 'Date' 'eq' 'DateTime.MinValue')) @('OR') 'The row asks for either the scalar null route or an explicit DateTime.MinValue sentinel value.' }
        'F199' { return New-Supported 'rows' @((New-Predicate 'dueDate' 'Date' 'inquarter' '1'), (New-Predicate 'dueDate' 'Date' 'inquarter' '4')) @('OR') 'The local either/or over quarters is represented as two date-component leaves.' }
        'F212' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("events").Index("eventDate").AsDateTime.MonthDay(month, day).EndCondition | catalog group: catalog["events"].Where("eventDate").AsDateTime.MonthDay(month, day).EndCondition' 'store.Where.PropPath(".EventDate").AsDateTime.MonthDay(month, day).EndCondition' 'The builder has direct month/day date component syntax for annual anniversary-style matching.' }
        'F248' { return New-Supported 'documents' @((New-Predicate 'parentDocumentId' 'Guid' 'null' ''), (New-Predicate 'parentDocumentId' 'Guid' 'eq' 'Guid.Empty')) @('OR') 'The row asks for either the scalar null route or an explicit Guid.Empty sentinel value.' }
        'F201' { return New-Supported 'appointments' @((New-Predicate 'startTime' 'Date' 'morning' '')) @() }
        'F202' { return New-Supported 'appointments' @((New-Predicate 'startTime' 'Date' 'afternoon' '')) @() }
        'F203' { return New-Supported 'appointments' @((New-Predicate 'startTime' 'Date' 'evening' '')) @() }
        'F204' { return New-Supported 'appointments' @((New-Predicate 'startTime' 'Date' 'night' '')) @() }
        'F257' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsInt32(0).EqualTo(42).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsInt32(0).EqualTo(42).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsInt32(0).EqualTo(42).EndCondition' 'Typed binary slice syntax matches the first four bytes interpreted as Int32.' }
        'F258' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsInt64(8).GreaterThan(0L).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsInt64(8).GreaterThan(0L).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsInt64(8).GreaterThan(0L).EndCondition' 'Typed binary slice syntax matches bytes 8 through 15 interpreted as Int64.' }
        'F259' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsUtf8String(offset, length).EqualTo("OK").EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsUtf8String(offset, length).EqualTo("OK").EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsUtf8String(offset, length).EqualTo("OK").EndCondition' 'Typed UTF-8 binary slice syntax matches the encoded string segment.' }
        'F260' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsUtf16String(offset, length).StartsWith("MSG").EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsUtf16String(offset, length).StartsWith("MSG").EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsUtf16String(offset, length).StartsWith("MSG").EndCondition' 'Typed UTF-16 binary slice syntax matches the encoded string prefix.' }
        'F261' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsDecimal(offset).GreaterThan(100m).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsDecimal(offset).GreaterThan(100m).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsDecimal(offset).GreaterThan(100m).EndCondition' 'Typed decimal binary slice syntax matches the requested numeric comparison.' }
        'F262' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsGuid(offset).EqualTo(guid).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsGuid(offset).EqualTo(guid).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsGuid(offset).EqualTo(guid).EndCondition' 'Typed Guid binary slice syntax matches the requested Guid equality.' }
        'F263' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsDateTime(offset).LessThan(cutoff).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsDateTime(offset).LessThan(cutoff).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsDateTime(offset).LessThan(cutoff).EndCondition' 'Typed DateTime binary slice syntax matches the cutoff comparison.' }
        'F264' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsTimeSpan(offset).GreaterThan(TimeSpan.FromMinutes(5)).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsTimeSpan(offset).GreaterThan(TimeSpan.FromMinutes(5)).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsTimeSpan(offset).GreaterThan(TimeSpan.FromMinutes(5)).EndCondition' 'Typed TimeSpan binary slice syntax matches the duration comparison.' }
        'F265' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsBigInt(offset, byteLength).LessThan(BigInteger.Zero).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsBigInt(offset, byteLength).LessThan(BigInteger.Zero).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsBigInt(offset, byteLength).LessThan(BigInteger.Zero).EndCondition' 'Typed BigInteger binary slice syntax matches the negative-value comparison.' }
        'F275' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("files").Index("payload").AsBinary.SlicedAsUInt16(typeCodeOffset).Between(minTypeCode, maxTypeCode).EndCondition | catalog group: catalog["files"].Where("payload").AsBinary.SlicedAsUInt16(typeCodeOffset).Between(minTypeCode, maxTypeCode).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsUInt16(typeCodeOffset).Between(minTypeCode, maxTypeCode).EndCondition' 'A two-byte type code is a typed binary slice, then an ordinary numeric range predicate.' }
        'F277' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("files").Index("payload").AsBinary.SlicedAsUInt16(headerFlagsOffset).AllBitsSet(requiredBit).EndCondition | catalog group: catalog["files"].Where("payload").AsBinary.SlicedAsUInt16(headerFlagsOffset).AllBitsSet(requiredBit).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsUInt16(headerFlagsOffset).AllBitsSet(requiredBit).EndCondition' 'A binary header flag is a typed numeric slice followed by a bitmask predicate.' }
        'F280' { return New-Supported 'files' @((New-Predicate 'footer' 'Binary' 'ends' 'expectedTrailer')) @() 'The binary footer check maps to the varlen binary suffix predicate.' }
        'F281' { return New-Supported 'people' @((New-Predicate 'firstName' 'String' 'starts' '"Jo"'), (New-Predicate 'lastName' 'String' 'eq' '"Smith"')) @('AND') }
        'F282' { return New-Supported 'people' @((New-Predicate 'firstName' 'String' 'eq' '"John"'), (New-Predicate 'lastName' 'String' 'eq' '"Smith"')) @('AND') }
        'F283' { return New-Supported 'people' @((New-Predicate 'firstName' 'String' 'eq' '"John"'), (New-Predicate 'firstName' 'String' 'eq' '"Jon"')) @('OR') }
        'F284' { return New-Supported 'people' @((New-Predicate 'firstName' 'String' 'starts' '"Jo"'), (New-Predicate 'lastName' 'String' 'starts' '"Sm"')) @('AND') }
        'F285' { return New-Supported 'people' @((New-Predicate 'lastName' 'String' 'eq' '"Smith"'), (New-Predicate 'age' 'Int64' 'gte' '18L')) @('AND') }
        'F286' { return New-Supported 'people' @((New-Predicate 'lastName' 'String' 'eq' '"Smith"'), (New-Predicate 'age' 'Int64' 'between' '18L' '64L')) @('AND') }
        'F287' { return New-Supported 'people' @((New-Predicate 'firstName' 'String' 'starts' '"A"'), (New-Predicate 'age' 'Int64' 'lt' '18L')) @('AND') }
        'F288' { return New-Supported 'people' @((New-Predicate 'lastName' 'String' 'in' 'new[] { "Smith", "Jones" }'), (New-Predicate 'age' 'Int64' 'gt' '65L')) @('AND') 'The local OR over last-name values is represented as same-index membership plus age comparison.' }
        'F299' { return New-Supported 'users' @((New-Predicate 'country' 'String' 'eq' '"US"'), (New-Predicate 'state' 'String' 'in' 'new[] { "AZ", "CA", "NV" }')) @('AND') }
        'F310' { return New-Supported 'orders' @((New-Predicate 'billingPostalCode' 'String' 'starts' '"85"'), (New-Predicate 'country' 'String' 'eq' '"US"')) @('AND') }
        'F312' { return New-Supported 'invoices' @((New-Predicate 'balanceDueCents' 'Int64' 'eq' '0L'), (New-Predicate 'paidAt' 'Date' 'notnull' '')) @('AND') }
        'F347' { return New-Supported 'logs' @((New-Predicate 'severity' 'String' 'in' 'new[] { "Error", "Critical" }'), (New-Predicate 'message' 'String' 'contains' '"timeout"')) @('AND') 'The natural language OR is local to severity values, so the condition uses severity membership plus message containment.' }
        'F421' { return New-Supported 'records' @((New-Predicate 'status' 'String' 'in' 'new[] { "Open", "Pending" }'), (New-Predicate 'priority' 'String' 'eq' '"High"')) @('AND') 'Grouped OR collapses to same-index membership plus priority equality.' }
        'F434' { return New-Supported 'records' @((New-Predicate 'amount' 'Int64' 'gt' '100L'), (New-Predicate 'amount' 'Int64' 'lte' '500L')) @('AND') 'Two bounds over the same numeric index are represented as explicit lower and upper range leaves.' }
        'F435' { return New-Supported 'records' @((New-Predicate 'date' 'Date' 'gte' 'startDate'), (New-Predicate 'date' 'Date' 'lt' 'endDate')) @('AND') 'The half-open date window uses an inclusive lower bound and exclusive upper bound.' }
        'F436' { return New-Supported 'records' @((New-Predicate 'version' 'Int64' 'gt' 'minimumVersion'), (New-Predicate 'version' 'Int64' 'notin' 'deprecatedVersions')) @('AND') 'Runtime variables and set operands are ordinary deferred values for numeric predicates.' }
        'F437' { return New-Supported 'records' @((New-Predicate 'flags' 'Int64' 'allbits' 'requiredBits'), (New-Predicate 'flags' 'Int64' 'nobits' 'forbiddenBits')) @('AND') 'The required and forbidden bit checks are both direct bitmask predicates over the stored flags key.' }
        'F438' { return New-Supported 'records' @((New-Predicate 'binaryHeader' 'Binary' 'starts' 'prefixBytes'), (New-Predicate 'status' 'String' 'eq' '"Active"')) @('AND') 'Binary prefix and status equality compose as ordinary indexed leaves.' }
        'F439' { return New-Supported 'records' @((New-Predicate 'guid' 'Guid' 'starts' 'guidPrefix'), (New-Predicate 'createdDate' 'Date' 'between' 'yearStart' 'yearEnd')) @('AND') 'Guid text-prefix matching can compose with a caller-supplied current-year date range.' }
        'F440' { return New-Supported 'records' @((New-Predicate 'stringSuffix' 'String' 'ends' 'suffix'), (New-Predicate 'numericScore' 'Int64' 'gt' 'threshold')) @('AND') 'Suffix and threshold variables are ordinary runtime operands for existing string and numeric predicates.' }
        'F451' { return New-Supported 'records' @((New-Predicate 'tenantId' 'Guid' 'eq' 'currentTenantId')) @() 'The caller tenant id is a runtime operand for a stored Guid key.' }
        'F452' { return New-Supported 'records' @((New-Predicate 'userId' 'Guid' 'eq' 'currentUserId')) @() 'The caller user id is a runtime operand for a stored Guid key.' }
        'F453' { return New-Supported 'records' @((New-Predicate 'value' 'Int64' 'gt' 'threshold')) @() 'A threshold comparison that uses a runtime variable is still an ordinary deferred numeric operand.' }
        'F454' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index(selectedIndexName).AsString.EqualTo(searchValue).EndCondition | catalog group: catalog["records"].Where(selectedIndexName).AsString.EqualTo(searchValue).EndCondition' 'store.Where.PropPath(selectedPropertyPath).AsString.EqualTo(searchValue).EndCondition' 'Runtime index selection is supported by the string index-name overload; the caller still owns choosing a valid indexed field.' }
        'F456' { return New-ManualSyntax 'ForGroup: var field = useCreatedDate ? "createdDate" : "updatedDate"; LibraDexCondition.ForGroup("records").Index(field).AsDateTime.LessThan(cutoff).EndCondition | catalog group: catalog["records"].Where(field).AsDateTime.LessThan(cutoff).EndCondition' 'store.Where.PropPath(useCreatedDate ? ".CreatedDate" : ".UpdatedDate").AsDateTime.LessThan(cutoff).EndCondition' 'The same cutoff can be applied to either indexed date field by selecting the indexed field before building the condition.' }
        'F496' { return New-Supported 'records' @((New-Predicate 'stringProperty' 'String' 'null' '')) @() }
        'F497' { return New-Supported 'records' @((New-Predicate 'stringProperty' 'String' 'empty' '')) @() }
        'F498' { return New-Supported 'records' @((New-Predicate 'stringProperty' 'String' 'nullorempty' '')) @() }
        'F499' { return New-Supported 'records' @((New-Predicate 'numericProperty' 'Int64' 'null' '')) @() }
        'F500' { return New-Supported 'records' @((New-Predicate 'numericProperty' 'Int64' 'notnull' ''), (New-Predicate 'numericProperty' 'Int64' 'gt' '0L')) @('AND') }
        'F501' { return New-Supported 'records' @((New-Predicate 'dateProperty' 'Date' 'null' '')) @() }
        'F502' { return New-Supported 'records' @((New-Predicate 'dateProperty' 'Date' 'notnull' ''), (New-Predicate 'dateProperty' 'Date' 'lt' 'today')) @('AND') }
        'F512' { return New-Supported 'records' @((New-Predicate 'phoneNumber' 'String' 'starts' 'countryCode')) @() 'A country-code prefix is an ordinary runtime string prefix operand.' }
        'F513' { return New-Supported 'records' @((New-Predicate 'phoneNumber' 'String' 'ends' 'localExtension')) @() 'A local extension suffix is an ordinary runtime string suffix operand.' }
        'F516' { return New-Supported 'records' @((New-Predicate 'geohash' 'String' 'starts' 'areaPrefix')) @() 'Geohash nearby-area narrowing is represented as a stored geohash-prefix predicate.' }
        'F519' { return New-Supported 'records' @((New-Predicate 'latitudeBucket' 'Int64' 'between' 'minLatitudeBucket' 'maxLatitudeBucket')) @() 'The latitude bucket is already modeled as an indexed integer range key.' }
        'F520' { return New-Supported 'records' @((New-Predicate 'longitudeBucket' 'Int64' 'between' 'minLongitudeBucket' 'maxLongitudeBucket')) @() 'The longitude bucket is already modeled as an indexed integer range key.' }
        'F522' { return New-Supported 'records' @((New-Predicate 'currency' 'String' 'eq' '"EUR"'), (New-Predicate 'amount' 'Int64' 'between' 'minMinorUnits' 'maxMinorUnits')) @('AND') 'Currency equality composes with the indexed minor-unit amount range.' }
        'F531' { return New-Supported 'records' @((New-Predicate 'ipAddress' 'String' 'eq' 'ipv4Address')) @() 'A specific IPv4 address is represented as the stored address key value.' }
        'F533' { return New-Supported 'records' @((New-Predicate 'ipAddress' 'String' 'notbetween' 'denyRangeStart' 'denyRangeEnd')) @() 'A single denylisted range can be represented as the negation of a stored address-key range.' }
        'F534' { return New-Supported 'records' @((New-Predicate 'ipv6Address' 'String' 'starts' 'networkPrefix')) @() 'An IPv6 network prefix is represented as a stored text-prefix predicate.' }
        'F543' { return New-Supported 'records' @((New-Predicate 'created' 'Date' 'between' 'now.AddDays(-14)' 'now')) @() 'The row defines recent as a fixed rolling 14-day created-date window.' }
        'F548' { return New-Supported 'records' @((New-Predicate 'tenantId' 'Guid' 'eq' 'currentTenantId')) @() 'Same-tenant semantics are direct when the row states the modeled key is tenant id.' }
        'F582' { return New-Supported 'records' @((New-Predicate 'nullableDate' 'Date' 'eq' 'DateTime.MinValue')) @() 'The row explicitly uses DateTime.MinValue as the stored sentinel value.' }
        'F583' { return New-Supported 'records' @((New-Predicate 'nullableGuid' 'Guid' 'eq' 'Guid.Empty')) @() 'The row explicitly uses Guid.Empty as the stored sentinel value.' }
        'F588' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'empty' '')) @() 'The binary varlen key state can distinguish empty byte array from missing or null binary value.' }
        'F608' { return New-Supported 'records' @((New-Predicate 'accessMask' 'Int64' 'allbits' 'requiredMask'), (New-Predicate 'accessMask' 'Int64' 'nobits' 'deniedMask')) @('AND') 'The access mask request maps to one required-bits predicate and one denied-bits predicate.' }
        'F621' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'contains' 'jsonContentTypeMarkerBytes')) @() 'The requested JSON content-type marker is represented as a byte sequence within the binary payload key.' }
        'F627' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'starts' 'payloadPrefix'), (New-Predicate 'tenantId' 'Guid' 'eq' 'tenantId')) @('AND') 'Binary payload prefix and tenant id equality compose as ordinary indexed leaves.' }
        'F642' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'null' '')) @() }
        'F643' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'empty' '')) @() }
        'F644' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'nullorempty' '')) @() }
        'F645' { return New-Supported 'records' @((New-Predicate 'name' 'String' 'nullorempty' ''), (New-Predicate 'name' 'String' 'starts' '"A"')) @('OR') }
        'F623' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsAsciiString(offset, length).EqualTo("OK").EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsAsciiString(offset, length).EqualTo("OK").EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsAsciiString(offset, length).EqualTo("OK").EndCondition' 'Typed ASCII binary slice syntax matches the requested OK field.' }
        'F624' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsUInt16(offset).BitAnd(flagMask, flagMask).EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsUInt16(offset).BitAnd(flagMask, flagMask).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsUInt16(offset).BitAnd(flagMask, flagMask).EndCondition' 'Typed UInt16 binary slice syntax matches the requested flag-bit test.' }
        'F625' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsDateOnly(offset).EqualTo(today).EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsDateOnly(offset).EqualTo(today).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsDateOnly(offset).EqualTo(today).EndCondition' 'Typed DateOnly binary slice syntax matches the requested date equality.' }
        'F626' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsDateTimeOffset(offset).LessThan(nowOffset).EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsDateTimeOffset(offset).LessThan(nowOffset).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsDateTimeOffset(offset).LessThan(nowOffset).EndCondition' 'Typed DateTimeOffset binary slice syntax matches the requested instant comparison; SlicedAsDateTime remains the explicit DateTime tick slice.' }
        'F646' { return [pscustomobject]@{ LibraDex='ForGroup: LibraDexCondition.ForGroup("users").Index("lastName").AsString.EqualTo("Smith").AND.Index("firstName").AsString.StartsWith("J").And.External(id => GetAge(id) > 18).EndCondition | catalog group: catalog["users"].Where("lastName").AsString.EqualTo("Smith").AndAlso("firstName").AsString.StartsWith("J").And.External(id => GetAge(id) > 18).EndCondition'; Abraxas='store.Where.PropPath(".LastName").AsString.EqualTo("Smith").AND.PropPath(".FirstName").AsString.StartsWith("J").AND.External(id => GetAge(id) > 18).EndCondition'; Score='5'; Notes='Anchored `.External(id => ...)` supplies the caller-owned age predicate after stored indexes narrow candidates.' } }
        'F650' { return [pscustomobject]@{ LibraDex='ForGroup: LibraDexCondition.ForGroup("users").External<int, long>(() => ageEntries).Between(18, 25).EndCondition where `ageEntries` yields `LibraDexExternalEntry<int, long>` or `KeyValuePair<int, long>` | typed catalog group: catalog["users"].Identities.Int64.External<int>(() => ageEntries).Between(18, 25).EndCondition'; Abraxas='store.Where.External<int>(() => ageEntries).Between(18, 25).EndCondition'; Score='5'; Notes='Runtime external entries behave as a caller-owned temporary index with typed key/identity pairs.' } }
        'F647' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").Index("firstName").AsString.StartsWith("Jo").And.External<int>(id => GetAges(id)).Between(18, 25).EndCondition | catalog group: catalog["users"].Where("firstName").AsString.StartsWith("Jo").And.External<int>(id => GetAges(id)).Between(18, 25).EndCondition' 'store.Where.PropPath(".FirstName").AsString.StartsWith("Jo").AND.External<int>(id => GetAges(id)).Between(18, 25).EndCondition' 'Correlated external key data uses candidate identities from the indexed first-name branch.' }
        'F648' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").Index("status").AsString.EqualTo("Active").Or.External(() => GetRecommendedUserIds()).EndCondition | catalog group: catalog["users"].Where("status").AsString.EqualTo("Active").Or.External(() => GetRecommendedUserIds()).EndCondition' 'store.Where.PropPath(".Status").AsString.EqualTo("Active").OR.External(() => GetRecommendedUserIds()).EndCondition' 'Standalone external identity sources can compose through Or.' }
        'F649' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").External(candidateIds).And.Index("status").AsString.EqualTo("Active").EndCondition | catalog group: catalog["users"].External(candidateIds).And.Index("status").AsString.EqualTo("Active").EndCondition' 'store.Where.External(candidateIds).AND.PropPath(".Status").AsString.EqualTo("Active").EndCondition' 'Caller-provided identity streams can intersect with indexed predicates.' }
        'F651' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").Index("lastName").AsString.EqualTo("Smith").And.External<int, long>(() => ageEntries).Between(18, 25).EndCondition | catalog group: catalog["users"].Where("lastName").AsString.EqualTo("Smith").And.External<int, long>(() => ageEntries).Between(18, 25).EndCondition' 'store.Where.PropPath(".LastName").AsString.EqualTo("Smith").AND.External<int>(() => ageEntries).Between(18, 25).EndCondition' 'Runtime external entries compose with stored indexes.' }
        'F652' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").Index("firstName").AsString.StartsWith("Jo").And.External((id, ordinal, isFirst) => MatchAgeFromCache(id, ordinal, isFirst)).EndCondition | catalog group: catalog["users"].Where("firstName").AsString.StartsWith("Jo").And.External((id, ordinal, isFirst) => MatchAgeFromCache(id, ordinal, isFirst)).EndCondition' 'store.Where.PropPath(".FirstName").AsString.StartsWith("Jo").AND.External((id, ordinal, isFirst) => MatchAgeFromCache(id, ordinal, isFirst)).EndCondition' 'The external identity predicate can initialize caller cache state on the first candidate.' }
        'F653' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").Index("firstName").AsString.StartsWith("Jo").And.External(ctx => Approves(ctx.Identity, ctx.Ordinal, ctx.IsFirst)).EndCondition | catalog group: catalog["users"].Where("firstName").AsString.StartsWith("Jo").And.External(ctx => Approves(ctx.Identity, ctx.Ordinal, ctx.IsFirst)).EndCondition' 'store.Where.PropPath(".FirstName").AsString.StartsWith("Jo").AND.External(ctx => Approves(ctx.Identity, ctx.Ordinal, ctx.IsFirst)).EndCondition' 'The context overload keeps named external predicates readable.' }
        'F654' { return [pscustomobject]@{ LibraDex='ForGroup: riskFragment = LibraDexCondition.ForGroup("users").Index("riskScore").AsInt64.GreaterOrEqual(80).Or.Index("flagged").AsBoolean.EqualTo(true).EndCondition; LibraDexCondition.ForGroup("users").Index("status").AsString.EqualTo("Active").And.Not.Group(riskFragment).EndCondition | catalog group: catalog["users"].Where("status").AsString.EqualTo("Active").And.Not.Group(riskFragment).EndCondition'; Abraxas='store.Where.PropPath(".Status").AsString.EqualTo("Active").AND.Not.Group(riskFragment).EndCondition'; Score='5'; Notes='Clause-level `.Not.Group(...)` exactly represents exclusion of a reusable fragment.' } }
        'F655' { return New-ManualSyntax 'ForGroup: nameFragment = LibraDexCondition.ForGroup("customers").Index("firstName").AsString.StartsWith("Jo").Or.Index("lastName").AsString.EqualTo("Jones").EndCondition; LibraDexCondition.ForGroup("customers").Group(nameFragment).And.Not.Index("status").AsString.EqualTo("Archived").EndCondition' 'store.Where.Group(nameFragment).AND.Not.PropPath(".Status").AsString.EqualTo("Archived").EndCondition' 'Completed-condition grouping composes a reusable name fragment with account-status exclusion.' }
        'F656' { return New-ManualSyntax 'ForGroup: catalog["users"].Where(ageIndex).GreaterOrEqual(18).AndAlso(statusIndex).EqualTo(1).EndCondition' 'store.Where.PropPath(".Age").AsInt64.GreaterOrEqual(18).AND.PropPath(".Status").AsInt32.EqualTo(1).EndCondition' 'Typed index handles carry key type and can skip redundant `.As...` selectors.' }
        'F657' { return New-ManualSyntax 'ForGroup: catalog["users"].Where(firstNameIndex).StartsWith("Jo", ignoreCase: true, culture: "en-US").AndAlso(lastNameIndex).EqualTo("Smith", ignoreCase: true, culture: "en-US").EndCondition' 'store.Where.PropPath(".FirstName").AsString.StartsWith("Jo", ignoreCase: true, culture: "en-US").AND.PropPath(".LastName").AsString.EqualTo("Smith", ignoreCase: true, culture: "en-US").EndCondition' 'Typed string index handles carry string operators without repeating `.AsString`.' }
        'F658' { return New-ManualSyntax 'ForGroup: catalog["events"].MultiKey(tenantIdIndex, occurredAtIndex).Where(0).AsGuid.EqualTo(tenantId).AndAlso(1).AsDateTime.Between(startUtc, endUtc).EndCondition' 'store.Where.PropPath(".TenantId").AsGuid.EqualTo(tenantId).AND.PropPath(".OccurredAt").AsDateTime.Between(startUtc, endUtc).EndCondition' 'Ordered MultiKey ordinal selectors match generated tenant/date conditions.' }
        'F659' { return New-ManualSyntax 'ForGroup: catalog["orders"].MultiKey(customerIdIndex, statusIndex).Where(0).AsGuid.EqualTo(customerId).AndAlso(1).Not.AsString.EqualTo("Cancelled").EndCondition' 'store.Where.PropPath(".CustomerId").AsGuid.EqualTo(customerId).AND.Not.PropPath(".Status").AsString.EqualTo("Cancelled").EndCondition' 'Ordered MultiKey selectors support canonical `.Not` before the selected value family.' }
        'F660' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("codes").Index("code").AsString.MatchesWith(@"[A-Z]\d-\d{4}", token).EndCondition | catalog group: catalog["codes"].Where("code").AsString.MatchesWith(@"[A-Z]\d-\d{4}", token).EndCondition' 'store.Where.PropPath(".Code").AsString.MatchesWith(@"[A-Z]\d-\d{4}", token).EndCondition' 'Regex whole-match comparison uses `.MatchesWith(pattern, value)`.' }
        'F661' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("codes").Index("code").AsString.MatchesWith(@"([A-Z]\d)-(\d{4})", suffix, 2).EndCondition | catalog group: catalog["codes"].Where("code").AsString.MatchesWith(@"([A-Z]\d)-(\d{4})", suffix, 2).EndCondition' 'store.Where.PropPath(".Code").AsString.MatchesWith(@"([A-Z]\d)-(\d{4})", suffix, 2).EndCondition' 'Regex capture comparison uses `.MatchesWith(pattern, value, groupNumber)`.' }
        'F662' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("codes").Index("code").AsString.MatchesInSet(@"([A-Z]\d)-(\d{4})", allowedSuffixes, 2).EndCondition | catalog group: catalog["codes"].Where("code").AsString.MatchesInSet(@"([A-Z]\d)-(\d{4})", allowedSuffixes, 2).EndCondition' 'store.Where.PropPath(".Code").AsString.MatchesInSet(@"([A-Z]\d)-(\d{4})", allowedSuffixes, 2).EndCondition' 'Regex capture membership uses `.MatchesInSet(pattern, values, groupNumber)`.' }
        'F663' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("codes").Index("code").AsString.NotMatchesInSet(@"([A-Z]\d)-(\d{4})", blockedSuffixes, 2).EndCondition | catalog group: catalog["codes"].Where("code").Not.AsString.MatchesInSet(@"([A-Z]\d)-(\d{4})", blockedSuffixes, 2).EndCondition' 'store.Where.PropPath(".Code").AsString.NotMatchesInSet(@"([A-Z]\d)-(\d{4})", blockedSuffixes, 2).EndCondition' 'Direct negative capture operators remain aliases for canonical `.Not`.' }
        'F664' { return New-Supported 'files' @((New-Predicate 'path' 'String' 'nullorempty' ''), (New-Predicate 'extension' 'String' 'neq' '"tmp"')) @('AND') }
        'F665' { return New-Supported 'readings' @((New-Predicate 'temperature' 'Int64' 'null' '')) @() }
        'F666' { return New-Supported 'readings' @((New-Predicate 'temperature' 'Int64' 'notnull' ''), (New-Predicate 'temperature' 'Int64' 'gt' 'threshold')) @('AND') }
        'F667' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("users").External<int, long>(() => agePairs).Between(18, 25).EndCondition where `agePairs` yields `KeyValuePair<int, long>` | typed catalog group: catalog["users"].Identities.Int64.External<int>(() => agePairs).Between(18, 25).EndCondition' 'store.Where.External<int>(() => agePairs).Between(18, 25).EndCondition' 'Runtime external entries accept KeyValuePair key/identity pairs.' }
        'F668' { return New-ManualSyntax 'ForGroup: catalog["sessions"].Identities.DateTime.External<int>(() => minuteBucketEntries).Between(startMinute, endMinute).EndCondition where `minuteBucketEntries` yields `LibraDexExternalEntry<int, DateTime>`' 'store.Where.External<int>(() => minuteBucketEntries).Between(startMinute, endMinute).EndCondition' 'DateTime identity selectors are explicit named identity-family stubs.' }
        'F669' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("documents").Index("tenantId").AsGuid.EqualTo(tenantId).And.External<int>(id => GetRankBuckets(id)).EqualTo(0).EndCondition | catalog group: catalog["documents"].Where("tenantId").AsGuid.EqualTo(tenantId).And.External<int>(id => GetRankBuckets(id)).EqualTo(0).EndCondition' 'store.Where.PropPath(".TenantId").AsGuid.EqualTo(tenantId).AND.External<int>(id => GetRankBuckets(id)).EqualTo(0).EndCondition' 'Correlated external keys derive rank buckets from narrowed candidate identities.' }
        'F670' { return New-ManualSyntax 'ForGroup: nameFragment = LibraDexCondition.ForGroup("users").Index("firstName").AsString.StartsWith("Jo").Or.Index("lastName").AsString.EqualTo("Jones").EndCondition; LibraDexCondition.ForGroup("users").External(() => recommendedIds).And.Group(nameFragment).EndCondition | catalog group: catalog["users"].External(() => recommendedIds).And.Group(nameFragment).EndCondition' 'store.Where.External(() => recommendedIds).AND.Group(nameFragment).EndCondition' 'Standalone external identity sources can compose with grouped indexed fragments.' }
        'F671' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("audit").Index("severity").AsInt32.GreaterOrEqual(3).And.External((id, ordinal, isFirst) => AuditCacheAllows(id, ordinal, isFirst)).EndCondition | catalog group: catalog["audit"].Where("severity").AsInt32.GreaterOrEqual(3).And.External((id, ordinal, isFirst) => AuditCacheAllows(id, ordinal, isFirst)).EndCondition' 'store.Where.PropPath(".Severity").AsInt32.GreaterOrEqual(3).AND.External((id, ordinal, isFirst) => AuditCacheAllows(id, ordinal, isFirst)).EndCondition' 'The external identity predicate can warm caller cache state on the first candidate.' }
        default { return $null }
    }
}

$rows = Import-Csv $Path
foreach ($row in $rows) {
    $manual = Complete-Manual $row.id
    $result = if ($manual) { $manual } else { Complete-General $row.natural_request $row.'dx quality score' }
    $row.'libradex condition(s)' = $result.LibraDex
    $row.'abraxas condition(s)' = $result.Abraxas
    $row.'dx quality score' = $result.Score
    $inventoryNote = if ($result.Score -eq '0') { Get-CapabilityInventoryNote $row.id } else { '' }
    $row.notes = if ($inventoryNote) { $inventoryNote } else { $result.Notes }
}

$rows | Export-Csv $Path -NoTypeInformation
$resolvedPath = (Resolve-Path $Path).Path
$csvText = [System.IO.File]::ReadAllText($resolvedPath)
$csvText = $csvText -replace "`r`n", "`n"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($resolvedPath, $csvText, $utf8NoBom)
$scoreGroups = $rows | Group-Object 'dx quality score' | Sort-Object Name
$scoreGroups | ForEach-Object { "$($_.Name)=$($_.Count)" }
