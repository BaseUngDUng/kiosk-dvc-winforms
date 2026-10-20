param(
    [int]$Port = 8248
)

$ServerRoot = $PSScriptRoot
$UrlPrefix = "http://+:$Port/"
$SecretKey = "VNPT@!56"

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($UrlPrefix)

try {
    $listener.Start()
} catch {
    # Neu khong chay quyen Admin, fallback ve localhost de test tren may ca nhan
    $listener = New-Object System.Net.HttpListener
    $UrlPrefix = "http://localhost:$Port/"
    $listener.Prefixes.Add($UrlPrefix)
    $listener.Start()
}

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  KIOSK AUTO-UPDATE SERVER DANG CHAY TAI:" -ForegroundColor Green
Write-Host "  -> Metadata: http://localhost:$Port/version.json" -ForegroundColor Yellow
Write-Host "  -> Package : http://localhost:$Port/KIOSK_Update.zip" -ForegroundColor Yellow
Write-Host "  -> Upload  : POST http://localhost:$Port/upload.ashx" -ForegroundColor Yellow
Write-Host "  -> Thu muc : $ServerRoot" -ForegroundColor Gray
Write-Host "  (Nhan Ctrl+C de dung server)" -ForegroundColor Gray
Write-Host "=====================================================" -ForegroundColor Cyan

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response

        $localPath = $request.Url.LocalPath.TrimStart('/')
        if ([string]::IsNullOrWhiteSpace($localPath)) {
            $localPath = "version.json"
        }

        # Ho tro day file tu xa qua HTTP POST /upload.ashx
        if ($request.HttpMethod -eq "POST" -and $localPath -eq "upload.ashx") {
            $apiKey = $request.Headers["X-Update-Key"]
            $fileName = $request.Headers["X-File-Name"]

            if ($apiKey -ne $SecretKey) {
                $response.StatusCode = 401
                $msg = [System.Text.Encoding]::UTF8.GetBytes('{"ok":false,"message":"Unauthorized: Sai X-Update-Key"}')
                $response.OutputStream.Write($msg, 0, $msg.Length)
                $response.OutputStream.Close()
                continue
            }

            if ($fileName -ne "KIOSK_Update.zip" -and $fileName -ne "version.json") {
                $response.StatusCode = 400
                $msg = [System.Text.Encoding]::UTF8.GetBytes('{"ok":false,"message":"Invalid X-File-Name"}')
                $response.OutputStream.Write($msg, 0, $msg.Length)
                $response.OutputStream.Close()
                continue
            }

            $destPath = Join-Path $ServerRoot $fileName
            $fs = [System.IO.File]::Open($destPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try {
                $request.InputStream.CopyTo($fs)
            } finally {
                $fs.Close()
            }

            $fileLen = (Get-Item $destPath).Length
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] UPLOAD $fileName ($fileLen bytes) -> $destPath" -ForegroundColor Green
            $response.ContentType = "application/json; charset=utf-8"
            $response.StatusCode = 200
            $okJson = "{`"ok`":true,`"file`":`"$fileName`",`"size`":$fileLen}"
            $msg = [System.Text.Encoding]::UTF8.GetBytes($okJson)
            $response.OutputStream.Write($msg, 0, $msg.Length)
            $response.OutputStream.Close()
            continue
        }

        $filePath = Join-Path $ServerRoot $localPath
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] GET $($request.Url.PathAndQuery) -> $filePath"

        if (Test-Path $filePath -PathType Leaf) {
            $bytes = [System.IO.File]::ReadAllBytes($filePath)
            if ($filePath.EndsWith(".json")) {
                $response.ContentType = "application/json; charset=utf-8"
            } elseif ($filePath.EndsWith(".zip")) {
                $response.ContentType = "application/zip"
            } else {
                $response.ContentType = "application/octet-stream"
            }
            $response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate")
            $response.ContentLength64 = $bytes.Length
            $response.StatusCode = 200
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        } else {
            $response.StatusCode = 404
            $msg = [System.Text.Encoding]::UTF8.GetBytes("404 Not Found: $localPath")
            $response.OutputStream.Write($msg, 0, $msg.Length)
        }
        $response.OutputStream.Close()
    }
} finally {
    $listener.Stop()
    $listener.Close()
}
