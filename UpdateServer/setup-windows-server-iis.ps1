param(
    [int]$Port = 248,
    [string]$SiteName = "KioskUpdateServer",
    [string]$PhysicalPath = "C:\KioskUpdateServer"
)

$ErrorActionPreference = "Stop"

Write-Host "=== CAI DAT KIOSK UPDATE SERVER TREN WINDOWS SERVER (PORT $Port) ===" -ForegroundColor Cyan

# 1. Tạo thư mục chứa file cập nhật trên Server và copy file sang
if (-not (Test-Path $PhysicalPath)) {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
}

Copy-Item -Path (Join-Path $PSScriptRoot "version.json") -Destination $PhysicalPath -Force
Copy-Item -Path (Join-Path $PSScriptRoot "KIOSK_Update.zip") -Destination $PhysicalPath -Force
Copy-Item -Path (Join-Path $PSScriptRoot "web.config") -Destination $PhysicalPath -Force
if (Test-Path (Join-Path $PSScriptRoot "upload.ashx")) {
    Copy-Item -Path (Join-Path $PSScriptRoot "upload.ashx") -Destination $PhysicalPath -Force
}
Write-Host "1. Da copy version.json, KIOSK_Update.zip, web.config, upload.ashx vao $PhysicalPath" -ForegroundColor Green

# 2. Cấp quyền Ghi (Modify) cho IIS trên thư mục C:\KioskUpdateServer để hỗ trợ đẩy bản cập nhật từ xa (upload.ashx)
try {
    & icacls $PhysicalPath /grant "IIS_IUSRS:(OI)(CI)M" /grant "IUSR:(OI)(CI)M" /grant "NETWORK SERVICE:(OI)(CI)M" /T /C | Out-Null
    Write-Host "2. Da cap quyen ghi cho IIS tren $PhysicalPath (ho tro Upload tu xa)" -ForegroundColor Green
} catch {
    Write-Host "2. Canh bao cap quyen icacls: $($_.Exception.Message)" -ForegroundColor Yellow
}

# 3. Bật tính năng ASP.NET 4.5 trên IIS (nếu chưa bật) để chạy được upload.ashx
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    Install-WindowsFeature -Name Web-Asp-Net45 -ErrorAction SilentlyContinue | Out-Null
}

# 4. Cấu hình IIS Website ở cổng 248
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (Get-Module -Name WebAdministration) {
    if (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) {
        Remove-Website -Name $SiteName
    }
    New-Website -Name $SiteName -Port $Port -PhysicalPath $PhysicalPath -Force | Out-Null
    Start-Website -Name $SiteName
    Write-Host "3. Da tao va khoi dong IIS Website '$SiteName' tai cong $Port" -ForegroundColor Green
} else {
    Write-Host "3. May chu chua bat IIS WebAdministration. Ban co the bat IIS hoac chay start-update-server.ps1 -Port $Port" -ForegroundColor Yellow
}

# 5. Mở cổng Firewall Windows cho port 248
$ruleName = "Allow_Kiosk_Update_Port_$Port"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
    Write-Host "4. Da mo cong Firewall TCP $Port ($ruleName)" -ForegroundColor Green
} else {
    Write-Host "4. Cong Firewall TCP $Port da duoc mo san" -ForegroundColor Green
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "HOAN TAT! Kiem tra tren trinh duyet tai:" -ForegroundColor Green
Write-Host "  -> http://localhost:$Port/version.json" -ForegroundColor Yellow
Write-Host "  -> http://113.161.12.112:$Port/version.json" -ForegroundColor Yellow
Write-Host "  -> Endpoint day file tu xa: http://113.161.12.112:$Port/upload.ashx" -ForegroundColor Yellow
Write-Host "=================================================================" -ForegroundColor Cyan
