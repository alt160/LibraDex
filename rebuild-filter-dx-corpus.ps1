param(
    [string]$Path = 'artifacts\filter-dx-natural-language-corpus.csv'
)

$ErrorActionPreference = 'Stop'

function ConvertTo-CamelName {
    param([string]$Phrase)

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
        'Date' { '.AsDate' }
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
        'weekend' { return ".Index(`"$Field`")$selector.IsWeekend()" }
        'weekday' { return ".Not.Index(`"$Field`")$selector.IsWeekend()" }
        'morning' { return ".Index(`"$Field`")$selector.IsMorning()" }
        'afternoon' { return ".Index(`"$Field`")$selector.IsAfternoon()" }
        'evening' { return ".Index(`"$Field`")$selector.IsEvening()" }
        'night' { return ".Index(`"$Field`")$selector.IsNight()" }
        'year' { return ".Index(`"$Field`")$selector.YearEqualTo($Argument)" }
        'month' { return ".Index(`"$Field`")$selector.MonthEqualTo($Argument)" }
        'yearmonth' { return ".Index(`"$Field`")$selector.YearMonth($Argument, $SecondArgument)" }
        'quarter' { return ".Index(`"$Field`")$selector.YearQuarter($Argument, $SecondArgument)" }
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

function Split-Criteria {
    param([string]$Text)

    $criteria = $Text.Trim().TrimEnd('.')
    if ($criteria -match '\(.+\)') { return $null }

    $protected = $criteria
    $protected = $protected -replace '(?i)greater than or equal to', 'greater than __LOCAL_OR__ equal to'
    $protected = $protected -replace '(?i)less than or equal to', 'less than __LOCAL_OR__ equal to'
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
    if ($p -match '(.+?)\s+(starts with|begins with)\s+a known byte prefix$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Binary' 'starts' 'prefixBytes'
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
    if ($p -match '(.+?)\s+is not\s+(".*?"|[A-Z][A-Za-z0-9_-]*)$' -or $p -match '(.+?)\s+is different from\s+(".*?"|[A-Z][A-Za-z0-9_-]*)$') {
        $field = ConvertTo-CamelName $Matches[1]
        $family = Get-KeyFamily $field $p
        return New-Predicate $field $family 'neq' (ConvertTo-Value $Matches[2] $family)
    }
    if ($p -match '(.+?)\s+is not\s+a specific GUID$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Guid' 'neq' 'guid'
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
    if ($p -match '(.+?)\s+is tomorrow$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' 'tomorrowStart' 'tomorrowEnd'
    }
    if ($p -match '(.+?)\s+is in the last\s+(\d+)\s+(days|hours|minutes)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return New-Predicate $field 'Date' 'between' "now.Add$($Matches[3].Substring(0,1).ToUpperInvariant() + $Matches[3].Substring(1))(-$($Matches[2]))" 'now'
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
    if ($p -match '(.+?)\s+has day-of-month equal to\s+(\d+)$') {
        $field = ConvertTo-CamelName $Matches[1]
        return ".Index(`"$field`").AsDate.DayEqualTo($($Matches[2]))"
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

    return $null
}

function Complete-General {
    param([string]$Request)

    $group = Get-GroupName $Request
    if ($Request -notmatch '^Find\s+.+?\s+where\s+(.+)$') {
        return New-Unsupported 'The request is not a condition-builder predicate; it describes result shaping, grouping, paging, mutation, or another terminal operation.'
    }

    $criteria = $Matches[1].Trim()
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
        'F144' { return New-Supported 'tasks' @((New-Predicate 'status' 'String' 'notin' 'new[] { "Closed", "Cancelled" }')) @() }
        'F150' { return New-Supported 'tasks' @((New-Predicate 'workflowState' 'String' 'null' ''), (New-Predicate 'workflowState' 'String' 'eq' '"Unknown"')) @('OR') }
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
        'F265' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("blobs").Index("payload").AsBinary.SlicedAsBigInteger(offset, byteLength).LessThan(BigInteger.Zero).EndCondition | catalog group: catalog["blobs"].Where("payload").AsBinary.SlicedAsBigInteger(offset, byteLength).LessThan(BigInteger.Zero).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsBigInteger(offset, byteLength).LessThan(BigInteger.Zero).EndCondition' 'Typed BigInteger binary slice syntax matches the negative-value comparison.' }
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
        'F496' { return New-Supported 'records' @((New-Predicate 'stringProperty' 'String' 'null' '')) @() }
        'F497' { return New-Supported 'records' @((New-Predicate 'stringProperty' 'String' 'empty' '')) @() }
        'F498' { return New-Supported 'records' @((New-Predicate 'stringProperty' 'String' 'nullorempty' '')) @() }
        'F499' { return New-Supported 'records' @((New-Predicate 'numericProperty' 'Int64' 'null' '')) @() }
        'F500' { return New-Supported 'records' @((New-Predicate 'numericProperty' 'Int64' 'notnull' ''), (New-Predicate 'numericProperty' 'Int64' 'gt' '0L')) @('AND') }
        'F501' { return New-Supported 'records' @((New-Predicate 'dateProperty' 'Date' 'null' '')) @() }
        'F502' { return New-Supported 'records' @((New-Predicate 'dateProperty' 'Date' 'notnull' ''), (New-Predicate 'dateProperty' 'Date' 'lt' 'today')) @('AND') }
        'F642' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'null' '')) @() }
        'F643' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'empty' '')) @() }
        'F644' { return New-Supported 'records' @((New-Predicate 'payload' 'Binary' 'nullorempty' '')) @() }
        'F645' { return New-Supported 'records' @((New-Predicate 'name' 'String' 'nullorempty' ''), (New-Predicate 'name' 'String' 'starts' '"A"')) @('OR') }
        'F623' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsAsciiString(offset, length).EqualTo("OK").EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsAsciiString(offset, length).EqualTo("OK").EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsAsciiString(offset, length).EqualTo("OK").EndCondition' 'Typed ASCII binary slice syntax matches the requested OK field.' }
        'F624' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsUInt16(offset).BitAnd(flagMask, flagMask).EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsUInt16(offset).BitAnd(flagMask, flagMask).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsUInt16(offset).BitAnd(flagMask, flagMask).EndCondition' 'Typed UInt16 binary slice syntax matches the requested flag-bit test.' }
        'F625' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsDateOnly(offset).EqualTo(today).EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsDateOnly(offset).EqualTo(today).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsDateOnly(offset).EqualTo(today).EndCondition' 'Typed DateOnly binary slice syntax matches the requested date equality.' }
        'F626' { return New-ManualSyntax 'ForGroup: LibraDexCondition.ForGroup("records").Index("payload").AsBinary.SlicedAsDateTimeOffset(offset).LessThan(now).EndCondition | catalog group: catalog["records"].Where("payload").AsBinary.SlicedAsDateTimeOffset(offset).LessThan(now).EndCondition' 'store.Where.PropPath(".Payload").AsBinary.SlicedAsDateTimeOffset(offset).LessThan(now).EndCondition' 'Typed DateTimeOffset binary slice syntax matches the requested instant comparison.' }
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
        'F658' { return New-ManualSyntax 'ForGroup: catalog["events"].MultiKey(tenantIdIndex, occurredAtIndex).Where(0).AsGuid.EqualTo(tenantId).AndAlso(1).AsDate.Between(startUtc, endUtc).EndCondition' 'store.Where.PropPath(".TenantId").AsGuid.EqualTo(tenantId).AND.PropPath(".OccurredAt").AsDate.Between(startUtc, endUtc).EndCondition' 'Ordered MultiKey ordinal selectors match generated tenant/date conditions.' }
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
    $result = if ($manual) { $manual } else { Complete-General $row.natural_request }
    $row.'libradex condition(s)' = $result.LibraDex
    $row.'abraxas condition(s)' = $result.Abraxas
    $row.'dx quality score' = $result.Score
    $row.notes = $result.Notes
}

$rows | Export-Csv $Path -NoTypeInformation
$scoreGroups = $rows | Group-Object 'dx quality score' | Sort-Object Name
$scoreGroups | ForEach-Object { "$($_.Name)=$($_.Count)" }
