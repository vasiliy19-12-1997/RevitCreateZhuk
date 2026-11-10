[CmdletBinding()]
param(
    [string] $AddinsDirectory = (Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024')
)

$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'dist\RevitCreateZhuk.dll'
$worker = Join-Path $PSScriptRoot 'dist\DwgReader\RevitCreateZhuk.DwgReader.exe'
if (-not (Test-Path -LiteralPath $dll) -or -not (Test-Path -LiteralPath $worker)) {
    throw 'Не найдена сборка в dist. Сначала выполните Build.ps1 или восстановите папку dist из комплекта.'
}

$escaped = [System.Security.SecurityElement]::Escape($dll)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitCreateZhuk</Name>
    <Assembly>$escaped</Assembly>
    <AddInId>6E8C5016-645A-4D8F-9142-3C3C82B67545</AddInId>
    <FullClassName>RevitCreateZhuk.RevitApplication</FullClassName>
    <VendorId>A101</VendorId>
    <VendorDescription>A101 BIM</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
New-Item -Path $AddinsDirectory -ItemType Directory -Force | Out-Null
$file = Join-Path $AddinsDirectory 'RevitCreateZhuk.addin'
$manifest | Set-Content -LiteralPath $file -Encoding UTF8
Write-Output "Установлено в $file. Перезапустите Revit 2024."
