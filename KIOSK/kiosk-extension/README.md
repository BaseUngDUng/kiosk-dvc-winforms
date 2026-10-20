# VNPT iGate Kiosk Ministry Dossier Monitor Extension

Extension này dùng để cài đặt trên trình duyệt Trạm Kiosk (Google Chrome / Microsoft Edge / Chrome Kiosk Shell) để **tự động phát hiện mã hồ sơ** khi công dân hoàn thành nộp hồ sơ trên các Cổng Dịch vụ công của Bộ/Ngành bên ngoài (những hệ thống không can thiệp được source code).

---

## 🚀 Hướng dẫn Cài đặt trên máy Kiosk

1. Trích xuất hoặc sao chép thư mục `kiosk-extension` lên máy Kiosk (hoặc đường dẫn `src/assets/kiosk-extension`).
2. Mở trình duyệt Chrome trên máy Kiosk.
3. Truy cập địa chỉ: `chrome://extensions/`
4. Bật công tắc **Developer mode** (Chế độ dành cho nhà phát triển) ở góc trên bên phải.
5. Nhấn nút **Load unpacked** (Tải tiện ích đã giải nén).
6. Chọn thư mục `kiosk-extension`.

---

## ⚡ Nguyên lý hoạt động

1. Khi công dân thao tác nộp hồ sơ trên trang Cổng DVC của Bộ (ví dụ Bộ Công An, Bộ GTVT, Bộ Tư Pháp...), Extension chạy ngầm script `content-script.js`.
2. Ngay khi trang chuyển hướng đến trang hoàn thành nộp (chứa thông tin như *"Nộp hồ sơ thành công"*, *"Mã hồ sơ: ..."*, `maHoSo=...`), Extension sẽ:
   - Tự động trích xuất `MaHoSo` bằng các mẫu biểu thức chính quy (Regex / URL parameters).
   - Phát sự kiện `window.postMessage` mang thông tin `KIOSK_MINISTRY_DOSSIER_SUBMITTED`.
3. Ứng dụng Kiosk nhận được sự kiện sẽ lập tức điều hướng về trang Callback hiển thị màn hình chúc mừng và ghi nhận log nộp hồ sơ Kiosk.
