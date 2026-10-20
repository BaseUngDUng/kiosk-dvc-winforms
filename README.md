# KIOSK DVC Quảng Trị (WinForms WebView2)

Ứng dụng Kiosk Dịch vụ công màn hình cảm ứng (WinForms + Microsoft Edge WebView2) tích hợp hệ thống tự động cập nhật (Auto-Update), định tuyến thủ tục Bộ/Ngành, theo dõi log `KioskUsing` và hỗ trợ từ xa qua UltraViewer.

---

## 1. Cấu trúc thư mục
- `KIOSK/Form1.cs`: Mã nguồn xử lý chính của ứng dụng WinForms Kiosk.
- `KIOSK/kiosk-extension/content-script.js`: Script tiêm vào WebView2 để nhận diện kết quả nộp hồ sơ trên các cổng Bộ/Ngành và đồng bộ về Kiosk.
- `KIOSK/App.config`: File cấu hình địa chỉ Web (`WEB1`, `WEB2`), API thống kê (`PADMAN_API`) và máy chủ cập nhật (`UPDATE_URL`).
- `UpdateServer/`:
  - `publish-new-version.ps1`: Script tự động hóa toàn bộ quy trình tăng version, biên dịch `MSBuild`, đóng gói `.zip` và đẩy bản cập nhật lên Update Server.
  - `upload.ashx` & `web.config`: Endpoint nhận bản cập nhật trên IIS Update Server.
  - `version.json`: Metadata phiên bản hiện tại trên Update Server.

---

## 2. Hướng dẫn Build & Đẩy bản cập nhật lên Server

Toàn bộ quá trình tăng version, biên dịch (`Release`, `Debug`, `Publish Installer`), đóng gói `.zip` và đẩy lên Update Server (`http://113.161.12.112:2480`) được thực hiện bằng **1 lệnh PowerShell duy nhất**:

```powershell
powershell -ExecutionPolicy Bypass -File ".\UpdateServer\publish-new-version.ps1" -NewVersion "1.0.0.17" -Description "Mô tả nội dung cập nhật"
```

> **Lưu ý:**
> - Nhớ tăng số `-NewVersion` lớn hơn phiên bản hiện tại trên server (xem trong `UpdateServer/version.json`).
> - Không cần sửa tay `AssemblyInfo.cs` hay `version.json`, script sẽ tự động cập nhật và đẩy `KIOSK_Update.zip` + `version.json` lên server.
> - Khi máy Kiosk ngoài xã/phường cập nhật tự động, các file cấu hình riêng của máy đó (`KIOSK.exe.config`, `App.config`, `kiosk_config.dat`) và file log 3 ngày (`kiosk_using.log`, `kiosk_using_history.jsonl`) sẽ được **giữ nguyên**, không bị ghi đè.

---

## 3. Các phím tắt & Nút chức năng trên máy Kiosk

| Thao tác / Phím tắt | Chức năng | Mật khẩu |
| :--- | :--- | :--- |
| **Nút `🏠 Home`** *(Góc dưới trái)* | Quay về Trang chủ Kiosk (giữ nguyên phiên đăng nhập hiện tại) | Không |
| **Nút `🎧 Hỗ trợ (UltraViewer)`** *(Góc dưới phải)* | Mở phần mềm **UltraViewer** nổi lên trên màn hình Kiosk để đọc ID/Pass hỗ trợ từ xa | Không |
| **`Ctrl + U`** | Phím tắt mở nhanh **UltraViewer** | Không |
| **`Ctrl + H`** *(hoặc vuốt từ mép trên màn hình xuống)* | Mở thanh công cụ quản trị (`Home`, `Refresh`, `Downloads`, `Print`, `⚙ Cấu hình ĐV`, `📋 Xem Log`, `🖥 UltraViewer`, `⬆ Update`, `Exit`) và bật bảng kiểm tra **Log KioskUsing** (tự động xóa log quá 3 ngày) | Không |
| **`Ctrl + M`** | Thu nhỏ ứng dụng Kiosk xuống Taskbar | `VNPT@!56` |
| **`Ctrl + Q`** hoặc **`Alt + F4`** | Thoát hoàn toàn ứng dụng Kiosk | `VNPT@!56` |
