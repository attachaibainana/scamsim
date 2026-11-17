<#
.SYNOPSIS
  Asks Codex (built-in image generation) to draw one game image from Art/gen/prompts/<Name>.txt.

.DESCRIPTION
  The brief is Art/gen/prompts/_style.txt + Art/gen/prompts/<Name>.txt. Codex saves the result to
  Art/gen/raw/<Name>.png (gitignored); tools/art/process.py turns raw images into game assets.
  An image that already exists is skipped unless -Force, so the script can be re-run after a
  usage limit. Codex's own transcript goes to Logs/art/<Name>.log.

.EXAMPLE
  powershell -File tools/art/gen.ps1 -Name bg_street
  powershell -File tools/art/gen.ps1 -Name police_stern -Ref Art/gen/raw/police_calm.png
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string[]]$Ref = @(),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$promptDir = Join-Path $repo 'Art\gen\prompts'
$rawDir = Join-Path $repo 'Art\gen\raw'
$logDir = Join-Path $repo 'Logs\art'
$out = Join-Path $rawDir "$Name.png"

if ((Test-Path $out) -and -not $Force) {
    Write-Output "skip $Name (already at $out)"
    exit 0
}

New-Item -ItemType Directory -Force -Path $rawDir, $logDir | Out-Null
$style = Get-Content (Join-Path $promptDir '_style.txt') -Raw -Encoding UTF8
$brief = Get-Content (Join-Path $promptDir "$Name.txt") -Raw -Encoding UTF8

$refNote = ''
if ($Ref.Count -gt 0) {
    $refNote = "Reference image(s) are attached: " + (($Ref | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') + ". Use them as described in the brief.`n"
}

$prompt = @"
Use your built-in image generation tool to create exactly ONE image from the brief below.
$refNote
Save the final image as a PNG file at exactly this path inside the repository: Art/gen/raw/$Name.png
Do not create, modify or delete any other file in the repository, and do not run git.
If the first attempt clearly breaks the brief (text in the image, a background that is not the flat colour asked for, the character cropped), generate it again before saving.
When done, reply with only the saved path.

BRIEF
$style
$brief
"@

$codexArgs = @('exec', '-C', $repo, '--sandbox', 'workspace-write', '--skip-git-repo-check',
    '--enable', 'image_generation', '-c', 'model_reasoning_effort="low"',
    '-o', (Join-Path $logDir "$Name.last.txt"))
foreach ($r in $Ref) { $codexArgs += "--image=$((Resolve-Path (Join-Path $repo $r)).Path)" }
$codexArgs += '-'

# Windows PowerShell pipes text to native programs as ASCII unless told otherwise.
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
# Codex writes progress to stderr; with 'Stop', Windows PowerShell would treat that as a failure.
$ErrorActionPreference = 'Continue'
$prompt | & codex @codexArgs *> (Join-Path $logDir "$Name.log")

if (Test-Path $out) {
    $size = [int]((Get-Item $out).Length / 1KB)
    Write-Output "ok ${Name} ($size KB)"
} else {
    Write-Output "FAILED ${Name}: no file at $out (see Logs/art/${Name}.log)"
    exit 1
}
