param(
    [Parameter(Mandatory = $true)] [string] $Report,
    [string] $Output = ""
)

$document = Get-Content -LiteralPath $Report -Raw -Encoding UTF8 | ConvertFrom-Json
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("## ExcelDB workbook diff")
$lines.Add("")

$entries = @($document.entries)
foreach ($kind in @("added", "removed", "renamed", "modified")) {
    $group = @($entries | Where-Object { $_.kind -eq $kind })
    if ($group.Count -eq 0) { continue }
    $lines.Add("### $kind ($($group.Count))")
    $lines.Add("")
    $lines.Add("| Table | Identity | Field | Before | After |")
    $lines.Add("| --- | --- | --- | --- | --- |")
    foreach ($entry in $group) {
        $before = ([string]$entry.before).Replace("|", "\|")
        $after = ([string]$entry.after).Replace("|", "\|")
        $lines.Add("| $($entry.tableId) | $($entry.identity) | $($entry.propertyPath) | $before | $after |")
    }
    $lines.Add("")
}

$markdown = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
if ([string]::IsNullOrWhiteSpace($Output)) {
    $markdown
} else {
    [System.IO.File]::WriteAllText((Resolve-Path -LiteralPath (Split-Path -Parent $Output)).Path + [System.IO.Path]::DirectorySeparatorChar + (Split-Path -Leaf $Output), $markdown, [System.Text.UTF8Encoding]::new($false))
}

