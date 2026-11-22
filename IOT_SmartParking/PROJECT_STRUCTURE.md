# CẤU TRÚC DỰ ÁN SMART PARKING IOT (PROJECT STRUCTURE)

> **Cập nhật ngày**: 22/11/2026  
> **Cấu trúc kiến trúc**: Đa vi điều khiển phân tán ESP32, .NET 8 Web API, SignalR WebSockets, Web PWA thuần HTML5/CSS3/JS.

---

```text
IOT_SmartParking/
├── backend/                               # MÃ NGUỒN MÁY CHỦ TRUNG TÂM (ASP.NET Core 8 Web API)
│   ├── Controllers/                       # Các API Controller xử lý HTTP REST endpoints
│   │   ├── AuthController.cs              # Đăng nhập, xác thực JWT, RBAC
│   │   ├── BookingController.cs           # Đặt chỗ, giữ ô, lịch sử đơn của khách hàng
│   │   ├── DemoController.cs              # 12 kịch bản tự động, quản lý log hệ thống, virtual slots
│   │   ├── DevicesController.cs           # API trạng thái 6 board vi điều khiển (/api/devices)
│   │   ├── GateController.cs              # Quét mã vào/ra, ALPR, mở khóa mã LOCKED, thu phạt OVERSTAY
│   │   ├── LichSuController.cs            # Lịch sử cảm biến, nhật ký hệ thống
│   │   ├── PaymentController.cs           # Webhook thanh toán VietQR, xác nhận tiền mặt CashCode
│   │   ├── PhienGuiXeController.cs        # Quản lý phiên gửi xe vào/ra thực tế
│   │   ├── PhuongTienController.cs        # Danh mục phương tiện của người dùng
│   │   ├── ThongKeController.cs           # Doanh thu ngày, tỷ lệ lấp đầy, số đơn trạng thái, xuất CSV
│   │   └── ViTriDoXeController.cs         # Sơ đồ 40 ô, chi tiết trạng thái từng vị trí
│   ├── Data/                              # Tầng truy cập dữ liệu Entity Framework Core
│   │   └── SmartParkingDbContext.cs       # DbContext mapping 13 bảng SQL Server
│   ├── Dtos/                              # Data Transfer Objects trao đổi giữa Client và Server
│   │   ├── BookingDtos.cs                 # DTO tạo đơn, danh sách đơn, thanh toán
│   │   └── GateDtos.cs                    # DTO quét barie, mở khóa mã, xác nhận phạt quá giờ
│   ├── Hubs/                              # Tầng truyền thông thời gian thực SignalR
│   │   └── ParkingHub.cs                  # WebSocket Hub đẩy sự kiện tới Web Dashboard
│   ├── Models/                            # Các thực thể dữ liệu (Database Entities)
│   │   ├── ChiTietViTriDo.cs              # Chi tiết lưu vết xe đỗ trong phiên
│   │   ├── DonDatCho.cs                   # Đơn đặt chỗ (Trạng thái, thời gian, cước phí)
│   │   ├── DonDatChoViTri.cs              # Chi tiết ô đỗ và biển số thuộc đơn
│   │   ├── LichSuCamBien.cs               # Lịch sử đo đạc của cảm biến siêu âm
│   │   ├── MaTienMat.cs                   # Mã CashCode 6 ký tự Crockford
│   │   ├── MaVaoCong.cs                   # Mã quét EntryCode check-in tại cổng
│   │   ├── NhatKyHeThong.cs               # Audit log lưu nhật ký sự kiện hệ thống
│   │   ├── PhienGuiXe.cs                  # Phiên gửi xe thực tế từ cổng vào tới cổng ra
│   │   ├── PhuongTien.cs                  # Thông tin xe, biển số
│   │   ├── TaiKhoan.cs                    # Tài khoản người dùng (Admin, Staff, Customer)
│   │   ├── ThanhToan.cs                   # Giao dịch thanh toán (VietQR, Tiền mặt)
│   │   ├── ThietBi.cs                     # Bản ghi 6 board ESP32 (GATE, ROW A-E)
│   │   └── ViTriDoXe.cs                   # 40 vị trí ô đỗ (A1..E8)
│   ├── Services/                          # Tầng nghiệp vụ xử lý nghiệp vụ chính
│   │   ├── GateService.cs                 # Xử lý luồng cổng, ALPR, điều phối ô, dẫn đường NeoPixel
│   │   ├── MqttBackgroundService.cs       # Background Service kết nối HiveMQ, LWT, RAM OVERLAP
│   │   ├── ParkingNotificationService.cs  # Đẩy sự kiện SignalR tới Web clients
│   │   └── ParkingTimeoutWorker.cs        # Background Worker quét tự động 4 loại timeout
│   ├── appsettings.json                   # Tham số cấu hình chính sách (ParkingPolicySettings)
│   └── Program.cs                         # Điểm khởi động ứng dụng, nạp DI, CORS, StaticFiles
│
├── dashboard/                             # GIAO DIỆN NGƯỜI DÙNG WEB & PWA THUẦN (HTML/CSS/JS)
│   ├── admin.html                         # Trang Quản trị: Sơ đồ 40 ô, 3 SVG Charts, 6 Board, xuất CSV
│   ├── customer.html                      # PWA Khách hàng: Đặt ô, thanh toán VietQR, vé QR, chính sách
│   ├── demo.html                          # Bảng DEMO bảo vệ: 12 kịch bản tự động, Test bench
│   ├── index.html                         # Trang Đăng nhập & Điều hướng vai trò (RBAC)
│   ├── staff.html                         # Bàn trực nhân viên: 4 mức ưu tiên (OVERSTAY, kẹt bãi, mở khóa)
│   ├── style.css                          # Phong cách giao diện responsive chuẩn mực
│   ├── sw.js                              # Service Worker hỗ trợ cài đặt PWA
│   └── manifest.json                      # Cấu hình PWA Manifest
│
├── src/                                   # MÃ NGUỒN FIRMWARE VI ĐIỀU KHIỂN (C++ Arduino ESP32)
│   └── main.cpp                           # Mã nguồn duy nhất cho cả 6 vai trò (NODE_ROLE phân tách)
│
├── wokwi/                                 # MÔI TRƯỜNG MÔ PHỎNG PHẦN CỨNG TRÊN WOKWI
│   ├── gate/                              # Board Cổng (esp32_gate)
│   │   ├── diagram.json                   # Sơ đồ mạch: ESP32, 2 Servo, LCD 1602, HC-SR04, 3 Nút
│   │   ├── wokwi.toml                     # Cấu hình nạp firmware.bin / firmware.elf
│   │   ├── firmware.bin                   # File nhị phân biên dịch sẵn cho board gate
│   │   └── firmware.elf                   # File biểu tượng debug cho board gate
│   └── row_e/                             # Board Hàng E (esp32_row_e)
│       ├── diagram.json                   # Sơ đồ mạch: ESP32, 8 HC-SR04 chung TRIG, NeoPixel
│       ├── wokwi.toml                     # Cấu hình nạp firmware.bin / firmware.elf
│       ├── firmware.bin                   # File nhị phân biên dịch sẵn cho board row_e
│       └── firmware.elf                   # File biểu tượng debug cho board row_e
│
├── test/                                  # KIỂM THỬ TỰ ĐỘNG (AUTOMATED TEST SUITE)
│   └── SmartParking.Tests/                # Dự án xUnit Test
│       ├── ParkingPhase1Tests.cs          # Kiểm thử phân quyền, đặt chỗ cơ bản, BOLA
│       ├── ParkingPhase2bTests.cs         # 32 bài kiểm thử chuyên sâu 16 tình huống nghiệp vụ
│       └── Mocks/                         # Mock dependencies và ScaledTimeProvider
│
├── tools/                                 # CÔNG CỤ HỖ TRỢ VẬN HÀNH & KIỂM THỬ
│   ├── SmartParking.Simulator/            # Ứng dụng Console giả lập toàn bộ phần cứng qua MQTT
│   │   └── Program.cs                     # Bộ phát kịch bản simulator độc lập
│   └── clean_retained_mqtt.py             # Script Python dọn sạch các topic retained rác trên HiveMQ
│
├── docs/                                  # TÀI LIỆU KỸ THUẬT VÀ BÁO CÁO BẢO VỆ ĐỒ ÁN
│   ├── bao-cao/                           # 6 Tài liệu chuẩn bị cho Hội đồng phản biện
│   │   ├── 01-tong-quan.md                # Mục tiêu, phạm vi, mô hình đặt chỗ, trạng thái, cấu hình
│   │   ├── 02-kien-truc.md                # Sơ đồ khối, bảng topic MQTT, ERD 13 bảng, lý do WiFi
│   │   ├── 03-thach-thuc-giai-phap.md     # 14+ tình huống nghiệp vụ, hậu quả, giải pháp, cách test
│   │   ├── 04-kich-ban-demo.md            # Kịch bản bảo vệ 10 phút và kế hoạch dự phòng 3 cấp
│   │   ├── 05-huong-dan-chay.md           # Hướng dẫn cài đặt từ đầu và bảng xử lý 6 sự cố
│   │   └── 06-cau-hoi-thuong-gap.md       # 20 câu hỏi phản biện của giảng viên và đáp án kỹ thuật
│   ├── BAO-CAO-TIEN-DO.md                 # Báo cáo tiến độ và bảng tổng hợp 16 tình huống kiểm thử
│   ├── mqtt-topics.md                     # Hợp đồng giao tiếp danh mục topic MQTT
│   ├── pin-map.md                         # Bảng phân bổ chân GPIO và an toàn strapping
│   ├── kien-truc-phan-cung.md             # Báo cáo phân tích phần cứng đa board phân tán
│   ├── wokwi-test.md                      # Hướng dẫn kiểm thử 2 board Wokwi song song
│   ├── circuit-isolation.md               # Thiết kế cách ly chống nhiễu nguồn
│   └── power-and-sleep.md                 # Phân tích công suất nguồn và chế độ năng lượng
│
├── platformio.ini                         # Cấu hình PlatformIO cho 6 môi trường (gate, row_a..e)
├── QL_BAIGIUXE_IOT.sql                    # Script SQL khởi tạo Database chính (Idempotent)
└── QL_BAIGIUXE_IOT_TEST.sql               # Script SQL khởi tạo Database kiểm thử độc lập
```
