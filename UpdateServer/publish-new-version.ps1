param(
    [Parameter(Mandatory=$false)]
    [string]$NewVersion = "1.0.0.7",
    [string]$Description = "Cap nhat phien ban moi Kiosk DVC",
    [string]$ServerUrl = "http://113.161.12.112:2480",
    [string]$UpdateKey = "VNPT@!56",
    [switch]$SkipUpload
)

$ErrorActionPreference = "Stop"
$KioskRoot = Split-Path -Parent $PSScriptRoot
$AssemblyInfoPath = Join-Path $KioskRoot "KIOSK\Properties\AssemblyInfo.cs"
$SlnPath = Join-Path $KioskRoot "KIOSK.sln"
$CsprojPath = Join-Path $KioskRoot "KIOSK\KIOSK.csproj"
$ReleaseDir = Join-Path $KioskRoot "KIOSK\bin\Release"
$PublishDir = Join-Path $KioskRoot "KIOSK\bin\Publish"
$ReleasePkgZip = Join-Path $KioskRoot "KIOSK\bin\Kiosk_Release_Package.zip"
$SetupPkgZip = Join-Path $KioskRoot "KIOSK\bin\Kiosk_Setup_Installer.zip"
$ZipOutPath = Join-Path $PSScriptRoot "KIOSK_Update.zip"
$VersionJsonPath = Join-Path $PSScriptRoot "version.json"

Write-Host "1. Cap nhat AssemblyVersion -> $NewVersion trong AssemblyInfo.cs..." -ForegroundColor Cyan
$asmContent = Get-Content $AssemblyInfoPath -Raw
$asmContent = $asmContent -replace '\[assembly:\s*AssemblyVersion\("[^"]+"\)\]', "[assembly: AssemblyVersion(`"$NewVersion`")]"
$asmContent = $asmContent -replace '\[assembly:\s*AssemblyFileVersion\("[^"]+"\)\]', "[assembly: AssemblyFileVersion(`"$NewVersion`")]"
Set-Content -Path $AssemblyInfoPath -Value $asmContent -Encoding UTF8

Write-Host "2. Bien dich KIOSK.sln (Release, Debug & Setup Installer)..." -ForegroundColor Cyan
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) {
    $msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
}
& $msbuild $SlnPath /p:Configuration=Release /t:Build /p:PublishDir="obj\Publish\\" /p:SignManifests=false /v:minimal
& $msbuild $SlnPath /p:Configuration=Debug /t:Build /p:PublishDir="obj\Publish\\" /p:SignManifests=false /v:minimal
& $msbuild $CsprojPath /p:Configuration=Release /t:Publish /p:PublishDir="$PublishDir\\" /p:ApplicationVersion="$NewVersion" /p:SignManifests=false /v:minimal

Copy-Item -Path (Join-Path $KioskRoot "KIOSK\kiosk-extension\*") -Destination (Join-Path $ReleaseDir "kiosk-extension\") -Force

Write-Host "3. Dong goi Kiosk_Release_Package.zip, Kiosk_Setup_Installer.zip va KIOSK_Update.zip..." -ForegroundColor Cyan
Compress-Archive -Path "$ReleaseDir\*" -DestinationPath $ReleasePkgZip -Force
Compress-Archive -Path "$PublishDir\*" -DestinationPath $SetupPkgZip -Force
Copy-Item -Path $ReleasePkgZip -Destination $ZipOutPath -Force

Write-Host "4. Cap nhat version.json -> $NewVersion..." -ForegroundColor Cyan
$versionObj = [ordered]@{
    version = $NewVersion
    downloadUrl = "KIOSK_Update.zip"
    overwriteConfig = $false
    description = $Description
}
$versionObj | ConvertTo-Json | Set-Content -Path $VersionJsonPath -Encoding UTF8

if (-not $SkipUpload -and -not [string]::IsNullOrWhiteSpace($ServerUrl)) {
    $uploadEndpoint = $ServerUrl.TrimEnd('/') + "/upload.ashx"
    Write-Host "5. Dang tu dong day KIOSK_Update.zip va version.json len Server ($uploadEndpoint)..." -ForegroundColor Cyan
    try {
        $headersZip = @{
            "X-Update-Key" = $UpdateKey
            "X-File-Name"  = "KIOSK_Update.zip"
        }
        $resZip = Invoke-RestMethod -Uri $uploadEndpoint -Method Post -InFile $ZipOutPath -Headers $headersZip -ContentType "application/octet-stream" -TimeoutSec 300
        Write-Host "   -> Da day KIOSK_Update.zip thanh cong! ($($resZip.size) bytes)" -ForegroundColor Green

        $headersJson = @{
            "X-Update-Key" = $UpdateKey
            "X-File-Name"  = "version.json"
        }
        $resJson = Invoke-RestMethod -Uri $uploadEndpoint -Method Post -InFile $VersionJsonPath -Headers $headersJson -ContentType "application/octet-stream" -TimeoutSec 30
        Write-Host "   -> Da day version.json (v$NewVersion) thanh cong!" -ForegroundColor Green
    } catch {
        Write-Host "   [!] Chua day duoc len $uploadEndpoint (Co the cong mang chua thong hoac chua cai upload.ashx tren Server): $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "   -> File da duoc tao san tai: $ZipOutPath" -ForegroundColor Yellow
    }
}

Write-Host "=====================================================" -ForegroundColor Green
Write-Host "HOAN TAT PHAT HANH BAN CAP NHAT v$NewVersion!" -ForegroundColor Green
Write-Host "Release  : $ReleasePkgZip" -ForegroundColor Yellow
Write-Host "Installer: $SetupPkgZip" -ForegroundColor Yellow
Write-Host "UpdateZip: $ZipOutPath" -ForegroundColor Yellow
Write-Host "Metadata : $VersionJsonPath" -ForegroundColor Yellow
Write-Host "=====================================================" -ForegroundColor Green
