[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$taskProject = Join-Path $PSScriptRoot 'MicPtt.csproj'
$taskTests = Join-Path $PSScriptRoot 'tests\PolicyTests\PolicyTests.csproj'
$taskOutput = Join-Path $PSScriptRoot 'dist'

Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet run --project $taskTests --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Policy tests failed with exit code $LASTEXITCODE."
    }
    & dotnet run --project (Join-Path $PSScriptRoot 'tests\OverlayTests\OverlayTests.csproj') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Overlay tests failed with exit code $LASTEXITCODE." }
    & dotnet run --project (Join-Path $PSScriptRoot 'tests\FeedbackAudioTests\FeedbackAudioTests.csproj') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Feedback audio tests failed with exit code $LASTEXITCODE." }

    & dotnet publish $taskProject --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None --output $taskOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-ko.txt') -Destination $taskOutput -Force
    Write-Host "Built: $(Join-Path $taskOutput 'MicKey.exe')"
}
finally {
    Pop-Location
}
