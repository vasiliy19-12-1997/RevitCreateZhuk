[CmdletBinding()]
param(
    [string] $Revit2024Dir,
    [string] $NuGetSource
)

$ErrorActionPreference = 'Stop'
if (-not $Revit2024Dir) {
    $Revit2024Dir = @(
        (Join-Path $env:ProgramFiles 'Autodesk\Revit 2024')
        'D:\Programs\Revit 2024'
    ) | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'RevitAPI.dll') } | Select-Object -First 1
}
if (-not $Revit2024Dir -or -not (Test-Path -LiteralPath (Join-Path $Revit2024Dir 'RevitAPIUI.dll'))) {
    throw 'Укажите каталог установленного Revit 2024 через -Revit2024Dir.'
}

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet завершился с кодом $LASTEXITCODE" }
}

Push-Location $PSScriptRoot
try {
    $worker = 'src/DwgReader/RevitCreateZhuk.DwgReader.csproj'
    $plugin = 'src/Revit/RevitCreateZhuk.csproj'
    $restore = @()
    if ($NuGetSource) { $restore += '--source', $NuGetSource, '-p:NuGetAudit=false' }
    Invoke-DotNet restore $worker @restore
    Invoke-DotNet restore $plugin @restore
    Invoke-DotNet publish $worker --no-restore -c Release --self-contained false -o dist/DwgReader
    Invoke-DotNet build $plugin --no-restore -c Release "-p:Revit2024Dir=$Revit2024Dir" -o dist
    Get-Item -LiteralPath 'dist/RevitCreateZhuk.dll'
}
finally {
    Pop-Location
}
