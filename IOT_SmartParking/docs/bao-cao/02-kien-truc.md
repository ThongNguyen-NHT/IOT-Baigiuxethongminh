# KIẾN TRÚC HỆ THỐNG VÀ MÔ HÌNH DỮ LIỆU SMART PARKING IOT

> **Dự án**: Smart Parking IoT System Architecture & Data Model  
> **Phiên bản**: 2.5 (Kiến trúc phân tán đa vi điều khiển WiFi/MQTT)

---

## 1. SƠ ĐỒ KHỐI TỔNG THỂ HỆ THỐNG

Hệ thống được thiết kế theo mô hình phân tán 3 tầng (Phần cứng IoT - Máy chủ xử lý trung tâm - Ứng dụng người dùng), giao tiếp thông qua hai kênh truyền tải thời gian thực: **MQTT** cho tầng thiết bị và **SignalR WebSockets** cho tầng giao diện người dùng.

```mermaid
flowchart TD
    subgraph HARDWARE["TẦNG THIẾT BỊ PHẦN CỨNG IOT (ESP32)"]
        GATE["Board ESP32 Gate (esp32_gate)<br>• Cảm biến cổng vào (TRIG 4, ECHO 5)<br>• Servo vào (13), Servo ra (12)<br>• LCD 1602 I2C 0x27 (21, 22)<br>• 3 Nút bấm: Ra (14), CP1 (18), CP2 (19)"]
        ROWA["Board Row A (esp32_row_a)<br>LED dải pixel 42-49"]
        ROWB["Board Row B (esp32_row_b)<br>LED dải pixel 34-41"]
        ROWC["Board Row C (esp32_row_c)<br>LED dải pixel 26-33"]
        ROWD["Board Row D (esp32_row_d)<br>LED dải pixel 18-25"]
        ROWE["Board Row E (esp32_row_e - Thật)<br>• 8 Cảm biến HC-SR04 (E1 - E8)<br>• Dải LED NeoPixel (pixel 10-17)"]
    end

    subgraph BROKER["TẦNG TRUYỀN THÔNG MQTT"]
        MQTT_BROKER["MQTT Broker (HiveMQ Cloud / Mosquitto)<br>Port: 1883 / 8883 (TCP)<br>Prefix: dacn-smartparking/lot1"]
    end

    subgraph BACKEND["TẦNG MÁY CHỦ TRUNG TÂM (ASP.NET CORE 8)"]
        MQTT_SVC["MqttBackgroundService<br>• Exponential Backoff Reconnect<br>• LWT Offline & Auto Resync Audit<br>• Bộ đệm RAM phát hiện OVERLAP"]
        WORKER["ParkingTimeoutWorker<br>• Quét HELD quá 5 phút<br>• Quét CHECK-IN quá T+15'<br>• Quét vào ô quá 10'<br>• Quét OVERSTAY"]
        GATE_SVC["GateService & BookingService<br>• Điều phối vào/ra, ALPR logic<br>• Phân bổ ô, dẫn đường NeoPixel"]
        DB[(SQL Server Database<br>QL_BAIGIUXE_IOT)]
        SIGNALR["ParkingHub (SignalR)<br>WebSockets / Long Polling"]
    end

    subgraph CLIENTS["TẦNG GIAO DIỆN NGƯỜI DÙNG (PWA WEB)"]
        ADMIN_WEB["admin.html (Quản trị viên)<br>Sơ đồ 40 ô, SVG Charts, 6 Board status"]
        STAFF_WEB["staff.html (Nhân viên trực)<br>4 Mức ưu tiên, Thu phạt, Mở khóa mã"]
        CUST_WEB["customer.html (Khách hàng)<br>Đặt chỗ, QR VietQR, Đèn dẫn đường"]
        DEMO_WEB["demo.html (Bảng Demo bảo vệ)<br>12 Kịch bản tự động, Test bench"]
    end

    GATE <-->|WiFi / MQTT| MQTT_BROKER
    ROWA <-->|WiFi / MQTT| MQTT_BROKER
    ROWB <-->|WiFi / MQTT| MQTT_BROKER
    ROWC <-->|WiFi / MQTT| MQTT_BROKER
    ROWD <-->|WiFi / MQTT| MQTT_BROKER
    ROWE <-->|WiFi / MQTT| MQTT_BROKER

    MQTT_BROKER <-->|MqttNet Client| MQTT_SVC
    MQTT_SVC --> GATE_SVC
    GATE_SVC <--> DB
    WORKER <--> DB
    WORKER --> SIGNALR
    GATE_SVC --> SIGNALR
    MQTT_SVC --> SIGNALR

    SIGNALR <-->|Realtime Socket| ADMIN_WEB
    SIGNALR <-->|Realtime Socket| STAFF_WEB
    SIGNALR <-->|Realtime Socket| CUST_WEB
    SIGNALR <-->|Realtime Socket| DEMO_WEB
```

---

## 2. HỢP ĐỒNG DANH MỤC TOPIC MQTT

Toàn bộ thông điệp trao đổi giữa Backend và Firmware tuân thủ chặt chẽ tiền tố `{prefix} = dacn-smartparking/lot1`:

| STT | Topic MQTT | Publisher | Subscriber | Retain | Ý nghĩa & Cấu trúc Payload mẫu |
|:---:|:---|:---:|:---:|:---:|:---|
| 1 | `{prefix}/gate/in/event` | ESP32 Gate | Backend | `false` | Báo xe qua cảm biến cổng vào: `{"event":"PASS","transitSec":2.4}` |
| 2 | `{prefix}/gate/out/event` | ESP32 Gate | Backend | `false` | Báo xe bấm nút ra cổng: `{"event":"BUTTON_EXIT_PRESSED"}` |
| 3 | `{prefix}/gate/{in\|out}/cmd` | Backend | ESP32 Gate | `false` | Lệnh điều khiển servo barie: `{"cmd":"OPEN","gate":"IN","durationSec":10}` |
| 4 | `{prefix}/slot/{id}/state` | ESP32 Row | Backend | **`true`** | Trạng thái khoảng cách cảm biến: `{"spot":"E1","distanceCm":75.2}` |
| 5 | `{prefix}/slot/{id}/cmd` | Backend | ESP32 Row | `false` | Lệnh màu LED ô đỗ: `"GREEN"`, `"RED"`, `"YELLOW"`, `"BLUE"`, `"OFF"` |
| 6 | `{prefix}/guide/cmd` | Backend | ESP32 (All) | `false` | Dẫn đường NeoPixel: `{"targetSlot":"E7","pixels":[0,1,10,11,16]}` |
| 7 | `{prefix}/display/cmd` | Backend | ESP32 Gate | `false` | Hiển thị chữ lên màn hình LCD: `{"line1":"CHAO MUNG","line2":"O DO: E7"}` |
| 8 | `{prefix}/device/{id}/status` | ESP32 / LWT | Backend | **`true`** | Trạng thái sống còn: `"ONLINE"` hoặc LWT `"OFFLINE"` |
| 9 | `{prefix}/device/{id}/power` | Simulator / Demo | Backend | **`true`** | Trạng thái nguồn điện của board: `"ONLINE"` hoặc `"OFFLINE"` |
| 10 | `{prefix}/device/{id}/cmd` | Backend | ESP32 Node | `false` | Lệnh gửi tới thiết bị: `{"cmd":"RESYNC"}` |
| 11 | `{prefix}/checkpoint/{id}/event` | ESP32 Gate | Backend | `false` | Nút bấm trạm dẫn đường: `{"event":"CHECKPOINT_TRIGGERED","cp":"CP1"}` |
| 12 | `{prefix}/alert` | Backend / ESP32 | Các bên | `false` | Cảnh báo sự cố: `{"event":"UNAUTHORIZED_ENTRY","message":"..."}` |
| 13 | `{prefix}/ack` | Backend | ESP32 Node | `false` | Phản hồi xác nhận: `{"topic":"...","status":"SUCCESS"}` |
| 14 | `{prefix}/summary` | Backend | Dashboard | **`true`** | Tổng quan bãi: `{"total":40,"free":35,"occupied":5}` |

---

## 3. MÔ HÌNH THỰC THỂ LIÊN KẾT (ERD 13 BẢNG CƠ SỞ DỮ LIỆU)

```mermaid
erDiagram
    TaiKhoan ||--o{ PhuongTien : "so huu"
    TaiKhoan ||--o{ DonDatCho : "tao don"
    PhuongTien ||--o{ PhienGuiXe : "tham gia"
    
    DonDatCho ||--o{ DonDatCho_ViTri : "phan bo o"
    DonDatCho ||--o{ ThanhToan : "thanh toan"
    DonDatCho ||--o| MaTienMat : "cap ma"
    DonDatCho ||--o{ MaVaoCong : "cap entry code"
    
    ViTriDoXe ||--o{ DonDatCho_ViTri : "gan vi tri"
    ViTriDoXe ||--o{ ChiTietViTriDo : "luu vet o do"
    ViTriDoXe ||--o{ LichSuCamBien : "ghi do sensor"
    
    PhienGuiXe ||--o{ ChiTietViTriDo : "chi tiet phien"
    
    DonDatCho ||--o{ NhatKyHeThong : "ghi audit"
    ThietBi ||--o{ ViTriDoXe : "phu trach hang"

    TaiKhoan {
        int MaTaiKhoan PK
        string TenDangNhap
        string MatKhauHash
        string VaiTro "Admin/Staff/Customer"
        string SoDienThoai
    }
    ViTriDoXe {
        string MaViTri PK "A1..E8"
        string Hang "A..E"
        int Cot "1..8"
        string TrangThai "FREE/HELD/BOOKED/OCCUPIED/OVERLAP/UNKNOWN"
        decimal KhoangCachCm
        bool LaViTriAo
    }
    DonDatCho {
        bigint MaDon PK
        int MaTaiKhoan FK
        string LoaiKhach "DAT_TRUOC/VANG_LAI"
        datetime ThoiGianHenT
        int GoiThoiLuongGio
        decimal TongTien
        string TrangThai "DRAFT/PENDING_PAYMENT/PAID/ENTERED/PARKED/COMPLETED/OVERSTAY"
        datetime HanThanhToan
        datetime HanCheckIn
        datetime HanDoVaoO
        decimal TienPhat
    }
    DonDatCho_ViTri {
        bigint MaChiTiet PK
        bigint MaDon FK
        string MaViTri FK
        string BienSo
        string TrangThai
    }
    PhienGuiXe {
        bigint MaPhien PK
        int MaPhuongTien FK
        datetime ThoiGianVao
        datetime ThoiGianRa
        string TrangThai "ACTIVE/COMPLETED"
        decimal TongTien
    }
    ChiTietViTriDo {
        bigint MaChiTiet PK
        bigint MaPhien FK
        string MaViTri FK
        datetime ThoiGianBatDau
        datetime ThoiGianKetThuc
    }
    ThanhToan {
        bigint MaThanhToan PK
        bigint MaDon FK
        string PhuongThuc "VIETQR/TIEN_MAT"
        decimal SoTien
        string TrangThai "PENDING/SUCCESS/FAILED"
    }
    MaTienMat {
        bigint MaTienMatId PK
        bigint MaDon FK
        string CashCode "Crockford 6 chars"
        string TrangThai "ACTIVE/REDEEMED/EXPIRED"
    }
    MaVaoCong {
        string MaEntryCode PK
        bigint MaDon FK
        string BienSo
        string TrangThai "ACTIVE/ENTERED/LOCKED"
        int SoLanQuet
    }
    ThietBi {
        string MaThietBi PK "esp32_gate, esp32_row_a..e"
        string Loai "GATE/ROW"
        string HangO "NULL hoac A..E"
        int PixelOffset
        int PixelCount
        string TrangThai "ONLINE/OFFLINE"
        datetime LanCapNhat
    }
    LichSuCamBien {
        bigint MaLichSu PK
        string MaViTri FK
        decimal KhoangCachCm
        string TrangThaiMoi
        datetime ThoiGianGhiNhan
    }
    NhatKyHeThong {
        bigint MaNhatKy PK
        bigint MaDon FK
        string LoaiSuKien
        string MucDo "INFO/WARNING/ERROR"
        string NoiDung
        datetime ThoiGianGhiNhan
    }
    PhuongTien {
        int MaPhuongTien PK
        int MaTaiKhoan FK
        string BienSoXe
        string LoaiXe
    }
```

---

## 4. LÝ DO CHỌN GIẢI PHÁP KHÔNG DÂY WIFI & GIAO THỨC MQTT

1. **Loại bỏ dây tín hiệu vật lý phức tạp (No Bus Wiring)**:
   - Trong bãi đỗ xe diện tích lớn từ hàng trăm đến hàng nghìn mét vuông, việc chạy dây truyền thông nối tiếp (RS-485, CAN bus, Modbus RTU) hoặc kéo cáp mạng Ethernet đòi hỏi đục đẽo sàn bê tông, tốn kém chi phí bảo trì và dễ bị đứt ngầm khi xe có tải trọng lớn đi qua.
   - WiFi kết hợp MQTT cho phép mỗi hàng ô chỉ cần cấp nguồn độc lập ($5V/2A$), các board vi điều khiển ESP32 hoàn toàn tự do lắp đặt và tự kết nối vào mạng không dây.
2. **Cơ chế Pub/Sub Decoupled (Bất đồng bộ & Mở rộng linh hoạt)**:
   - Các vi điều khiển không cần biết địa chỉ IP của nhau hoặc của Backend. Khi muốn thêm hàng ô mới (Row F, Row G...), chỉ việc flash firmware cấu hình `NODE_ROLE` tương ứng mà không phải sửa một dòng mã nguồn nào của các board khác hay khởi động lại Backend.
3. **Cơ chế LWT (Last Will and Testament) phát hiện mất điện tức thời**:
   - Khác với HTTP REST cần thăm dò (polling) liên tục gây quá tải mạng, MQTT tích hợp sẵn bản tin LWT do Broker tự động phát ra ngay khi ESP32 mất kết nối hoặc mất nguồn đột ngột. Backend lập tức đánh dấu các ô thuộc hàng đó sang `UNKNOWN`, bảo vệ tính nhất quán dữ liệu.
4. **Bản tin Retained giữ trạng thái bền vững**:
   - Trạng thái ô (`slot/{id}/state`) và trạng thái thiết bị (`device/{id}/status`) được lưu lại bản sao mới nhất trên Broker. Thiết bị hoặc Backend sau khi khởi động lại chỉ cần subscribe là ngay lập tức nhận được trạng thái toàn bãi mà không cần quét lại từ đầu.

---

## 5. CÁC GIỚI HẠN CỦA MÔ HÌNH VÀ HƯỚNG PHÁT TRIỂN

| Hạng mục | Hiện trạng mô hình thực nghiệm | Lý do thiết kế & Giải pháp thực tế khi triển khai thương mại |
|:---|:---|:---|
| **Hàng ô ảo (A, B, C, D)** | 32 ô hàng A-D là ô ảo trên phần mềm, chỉ hàng E có cảm biến thật. | Tối ưu chi phí thực nghiệm đồ án; kiến trúc code đã tách biệt theo 6 env PlatformIO và sẵn sàng nạp thẳng vào 5 board ESP32 phần cứng bổ sung. |
| **Nhận diện biển số (ALPR)** | Mô phỏng qua nút bấm & API `/api/gate/in/scan` (so khớp biển số quét mã với biển số đăng ký). | Thực tế sẽ kết nối Camera IP RTSP và module AI OCR (như OpenCV / Yolov8 / ANPR Cloud) để tự động nhận dạng chuỗi biển số. |
| **Thanh toán ngân hàng** | Mô phỏng webhook ngân hàng qua API Demo `/simulate-success`. | Thực tế sẽ kết nối cổng thanh toán thật (VNPay / MoMo / PayOS / VietQR Open API) có chữ ký số HMAC-SHA256 chuẩn bảo mật ngân hàng. |
| **MQTT Broker** | Sử dụng Broker công cộng `broker.hivemq.com:1883`. | Phục vụ demo không cần cài broker local; khi lên môi trường Production sẽ chạy cụm EMQX / Eclipse Mosquitto nội bộ có xác thực TLS/MQTTS và User/Password. |
