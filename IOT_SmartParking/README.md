# ĐỒ ÁN IOT SMART PARKING (HỆ THỐNG BÃI ĐỖ XE THÔNG MINH)

Hệ thống bãi đỗ xe thông minh kết hợp đặt chỗ trước (theo giờ) và khách vãng lai, dẫn đường bằng dải LED NeoPixel, kiểm soát ra vào 2 bước, chống lạm dụng thời gian và hoạt động mô phỏng toàn diện trên Wokwi & .NET 8 Web API.

---

## 📚 BỘ TÀI LIỆU BÁO CÁO BẢO VỆ ĐỒ ÁN (DOCS/BAO-CAO/)

Toàn bộ báo cáo kỹ thuật và hồ sơ bảo vệ đồ án được tổ chức tại thư mục [docs/bao-cao/](docs/bao-cao/):

1. 📄 [docs/bao-cao/01-tong-quan.md](docs/bao-cao/01-tong-quan.md): **Tổng quan hệ thống & Nghiệp vụ bãi xe**  
   *Mục tiêu, phạm vi, mô hình đặt chỗ trả trước kèm khách vãng lai, bảng chuyển trạng thái vị trí ô (`ViTriDoXe`), bảng chuyển trạng thái đơn hàng (`DonDatCho`), các tham số cấu hình chính sách (`ParkingPolicySettings`).*
2. 📄 [docs/bao-cao/02-kien-truc.md](docs/bao-cao/02-kien-truc.md): **Kiến trúc phân tán & Mô hình dữ liệu**  
   *Sơ đồ khối 3 tầng (Phần cứng 6 board ESP32 - Backend ASP.NET Core - Web SignalR), hợp đồng danh mục topic MQTT, sơ đồ ERD 13 bảng SQL Server, phân tích lý do chọn mạng không dây WiFi/MQTT thay vì RS-485/CAN, giới hạn mô hình.*
3. 📄 [docs/bao-cao/03-thach-thuc-giai-phap.md](docs/bao-cao/03-thach-thuc-giai-phap.md): **Thách thức kỹ thuật & Giải pháp xử lý**  
   *Phân tích chi tiết 14+ tình huống theo cấu trúc chuẩn (Tình huống - Hậu quả - Giải pháp - Cách kiểm thử): Chống đặt trùng ô, giữ chỗ không thanh toán, đến muộn NO_SHOW, quét mã bỏ đi, vào rồi ra ngay, bãi đông kẹt xe, hết hạn vào ô, đỗ lấn vạch OVERLAP, đỗ chui, mất điện LWT, quá giờ OVERSTAY, thanh toán trùng Idempotency, tranh chấp đồng thời.*
4. 📄 [docs/bao-cao/04-kich-ban-demo.md](docs/bao-cao/04-kich-ban-demo.md): **Kịch bản Demo bảo vệ 10 phút & Kế hoạch dự phòng**  
   *Kịch bản từng phút từng thao tác trên các màn hình; kế hoạch dự phòng 3 cấp độ (Bảng DEMO 12 kịch bản `demo.html`, Simulator console, Test suite tự động) khi mạng trường hoặc Wokwi gặp sự cố.*
5. 📄 [docs/bao-cao/05-huong-dan-chay.md](docs/bao-cao/05-huong-dan-chay.md): **Hướng dẫn cài đặt từ đầu & Xử lý sự cố**  
   *Cài đặt SQL Server instance, user-secrets, dotnet, PlatformIO, Wokwi; thứ tự khởi động chuẩn xác; bảng tổng hợp xử lý 6 sự cố kỹ thuật phổ biến (cổng 5137 bận, sqlcmd instance, wokwi.toml path, retained rác...).*
6. 📄 [docs/bao-cao/06-cau-hoi-thuong-gap.md](docs/bao-cao/06-cau-hoi-thuong-gap.md): **Tổng hợp 20 câu hỏi phản biện & Đáp án kỹ thuật**  
   *20 câu hỏi trọng tâm thường gặp từ Hội đồng phản biện kèm câu trả lời ngắn gọn, chuẩn xác dựa trên mã nguồn thực tế.*
7. 📄 [docs/BAO-CAO-TIEN-DO.md](docs/BAO-CAO-TIEN-DO.md): **Báo cáo tiến độ & Kết quả kiểm thử thực nghiệm**  
   *Ghi nhận kết quả thực tế từ dotnet build, 32 unit tests đạt 100%, biên dịch 6 môi trường PlatformIO và kiểm thử 12 kịch bản 3 lượt liên tục.*

---

## 1. TÀI KHOẢN DEMO VÀ PHÂN QUYỀN (RBAC)

> [!NOTE]
> Dữ liệu tài khoản mẫu dưới đây chỉ được tự động kích hoạt/seed khi chạy trong môi trường **`Development`** hoặc khi cờ cấu hình **`ParkingPolicy:DemoMode = true`**. Mật khẩu lưu trong cơ sở dữ liệu đã được băm an toàn bằng thuật toán **PBKDF2-SHA256 (100,000 vòng lặp)**.

| Tên đăng nhập | Mật khẩu mặc định | Vai trò (Role) | Chức năng chính |
|---|---|:---:|---|
| **`admin`** | `Admin@123` | **`Admin`** | Toàn quyền cấu hình tham số, xem thống kê doanh thu, quản lý danh mục ô đỗ, điều khiển cưỡng bức thanh chắn barie. |
| **`staff01`** | `Staff@123` | **`Staff`** | Nhân viên trực bãi: Xác nhận thu tiền mặt và cấp mã `CashCode`, giám sát luồng xe qua cổng, xử lý đỗ xe lệch ô và duyệt gia hạn khẩn cấp. |
| **`customer01`** | `Customer@123` | **`Customer`** | Khách hàng 1: Đặt chỗ trước theo gói giờ (1h, 2h, 4h), thanh toán QR / tiền mặt, nhận mã mở cổng `EntryCode`, xem chỉ dẫn đường và bấm "Tôi đang kẹt". |
| **`customer02`** | `Customer02@123` | **`Customer`** | Khách hàng 2: Dùng kiểm thử chéo quyền sở hữu (BOLA), thử nghiệm đặt chỗ đồng thời và xác minh tính toàn vẹn dữ liệu giữa các khách hàng. |

---

## 2. QUY ƯỚC THỜI GIAN & GIỜ HOẠT ĐỘNG BÃI ĐỖ XE

- **Lưu trữ Database**: Mọi mốc thời gian lưu trong cơ sở dữ liệu SQL Server đều quy về chuẩn **UTC** (`DATETIME2(0)`).
- **Giao tiếp API**: API nhận và phản hồi theo chuẩn **ISO 8601** kèm thông tin múi giờ / offset (ví dụ: `2026-11-20T10:00:00Z` hoặc `+07:00`).
- **Giao diện người dùng (Dashboard/Mobile)**: Luôn chuyển đổi và hiển thị trực quan theo giờ địa phương Việt Nam (**UTC+7**).
- **Giờ hoạt động của bãi đỗ xe**:
  - Cấu hình qua hai tham số: `ParkingPolicy:OpenHour` (mặc định: `6`) và `ParkingPolicy:CloseHour` (mặc định: `23`).
  - Hệ thống tự động chuyển đổi giờ hẹn sang giờ Việt Nam để kiểm tra. Mọi yêu cầu đặt chỗ diễn ra ngoài khung giờ **06:00 - 23:00** hoặc giờ hẹn nằm trong quá khứ sẽ bị từ chối với mã lỗi `400 Bad Request`.

---

## 3. KIỂM SOÁT QUYỀN TRUY CẬP ĐƠN HÀNG (OWASP API SECURITY - BOLA)

Hệ thống áp dụng nghiêm ngặt nguyên tắc kiểm soát quyền truy cập theo đối tượng (Broken Object Level Authorization):
- Khách hàng (`Customer`) chỉ được phép truy cập, thanh toán hoặc hủy các đơn đặt chỗ thuộc quyền sở hữu của chính mình.
- Nhân viên trực bãi (`Staff`) và Quản trị viên (`Admin`) có quyền truy cập tất cả các đơn hàng trong hệ thống.
- **Tại sao chọn mã lỗi 404 Not Found thay vì 403 Forbidden khi khách hàng truy cập đơn của người khác?**
  - Theo chuẩn bảo mật **OWASP API Security Top 10 (API1:2023 - BOLA)**, nếu API phản hồi `403 Forbidden`, hệ thống sẽ vô tình xác nhận cho kẻ tấn công biết mã đơn hàng (`maDon`) đó **CÓ TỒN TẠI** trong cơ sở dữ liệu (lỗ hổng rà quét mã đơn - *ID Enumeration*).
  - Phản hồi `404 Not Found` đối với đơn không thuộc sở hữu của người gọi sẽ che giấu hoàn toàn sự tồn tại của đơn hàng, triệt tiêu nguy cơ rà quét dữ liệu của khách hàng khác.

---

## 4. CẤU HÌNH BẢO MẬT BẰNG .NET USER-SECRETS

Để đảm bảo an toàn thông tin và không lưu lộ secret lên Git repository, toàn bộ chuỗi kết nối và khóa bí mật của Backend phải được cấu hình bằng công cụ **.NET Secret Manager (`dotnet user-secrets`)** cho từng máy lập trình viên.

### Các lệnh cấu hình Secret bắt buộc:
Di chuyển terminal vào thư mục `backend/` và chạy các lệnh sau:

```powershell
cd E:\IOT\IOTCODE\IOT_SmartParking\backend

# 1. Khởi tạo User Secrets cho dự án (nếu chưa khởi tạo)
dotnet user-secrets init

# 2. Cấu hình Chuỗi kết nối Database SQL Server cục bộ (SQL Express)
dotnet user-secrets set "ConnectionStrings:SmartParkingConnection" "Server=.\SQLEXPRESS;Database=QL_BAIGIUXE_IOT;Trusted_Connection=True;TrustServerCertificate=True;"

# 3. Cấu hình JWT Secret Key (Tối thiểu 32 ký tự UTF-8)
dotnet user-secrets set "Jwt:Key" "SmartParking_Production_SecretKey_Minimum_32_Chars_Long_Random!"

# 4. Cấu hình Webhook Secret Key cho HMAC-SHA256 (Tối thiểu 16 ký tự UTF-8)
dotnet user-secrets set "ParkingPolicy:WebhookSecret" "SmartParking_Production_WebhookSecret_Key_For_Hmac_2026!"

# 5. Cấu hình tài khoản MQTT Broker (nếu broker yêu cầu xác thực)
dotnet user-secrets set "MqttSettings:Username" "my_broker_username"
dotnet user-secrets set "MqttSettings:Password" "my_broker_password"
```

### Danh sách các khóa cấu hình bắt buộc:
Khi Backend khởi động, hệ thống sẽ kiểm tra nghiêm ngặt sự tồn tại của các khóa sau. Nếu thiếu bất kỳ khóa nào, Backend sẽ từ chối khởi động và báo lỗi cụ thể nêu rõ tên khóa còn thiếu:
1. `ConnectionStrings:SmartParkingConnection`: Chuỗi kết nối tới cơ sở dữ liệu `QL_BAIGIUXE_IOT`.
2. `Jwt:Key`: Khóa ký JSON Web Token (phải có độ dài từ 32 bytes trở lên).
3. `Jwt:Issuer`: Tên định danh tổ chức cấp token (mặc định: `SmartParking.Api`).
4. `Jwt:Audience`: Đối tượng sử dụng token (mặc định: `SmartParking.Dashboard`).
5. `ParkingPolicy:WebhookSecret`: Khóa bí mật dùng cho tính toán chữ ký số HMAC-SHA256 xác thực webhook ngân hàng (tối thiểu 16 bytes).
6. `ParkingPolicy:DemoMode`: Mặc định `false` trong `appsettings.json`, chỉ được bật thành `true` trong môi trường `Development` hoặc qua `user-secrets`. Khi bật, hệ thống in banner cảnh báo bảo mật nổi bật trên terminal lúc khởi động.

---

## 5. HƯỚNG DẪN KIỂM THỬ API BẰNG FILE .HTTP

1. Cài đặt extension **REST Client** (của Huachao Mao) trong Visual Studio Code.
2. Khởi động Backend API: `dotnet run --project backend/SmartParking.Api.csproj`.
3. Mở file `backend/SmartParking.Api.http` và nhấn nút **`Send Request`** tuần tự từ mục 1 đến mục 5:
   - **Mục 1**: Đăng nhập 4 tài khoản (`customer01`, `customer02`, `staff01`, `admin`), JWT token sẽ tự động lưu vào biến môi trường.
   - **Mục 2**: Đặt chỗ giữ ô với thời gian động `{{$datetime iso8601 2 h}}`, kiểm tra bắt lỗi trùng lặp (409 Conflict) và đặt ngoài giờ / quá khứ (400 Bad Request).
   - **Mục 3**: Kiểm thử 2 luồng thanh toán chuyển khoản:
     - *Luồng A*: Endpoint mô phỏng Demo `POST /api/demo/payment/{maDon}/simulate-success` (chỉ bật khi `DemoMode = true`, kiểm tra quyền sở hữu).
     - *Luồng B*: Webhook ngân hàng thật `POST /api/payment/webhook/bank-transfer` (bắt buộc chữ ký số `X-Signature` HMAC-SHA256 và `X-Timestamp` chống replay).
   - **Mục 4**: Thanh toán tiền mặt bằng mã `CashCode` (6 ký tự Crockford Base32 không tiền tố, băm HMAC-SHA256, nhân viên Staff sinh mã, khách hàng quy đổi, tự động khóa sau 5 lần nhập sai).
   - **Mục 5**: Hủy đơn đặt chỗ khi `PENDING_PAYMENT` (tự động giải phóng ô HELD về FREE) và kiểm tra lỗi 404 khi truy cập trái phép đơn của người khác.

---

## 6. CÁC LỆNH PHÁT TRIỂN & CHẠY DỰ ÁN

| Thao tác | Thư mục | Lệnh thực thi |
|---|---|---|
| **Build Backend API** | `backend/` | `dotnet build SmartParking.Api.csproj` |
| **Chạy Backend API** | `backend/` | `dotnet run --project SmartParking.Api.csproj` |
| **Chạy Simulator MQTT** | `tools/SmartParking.Simulator` | `dotnet run --project tools/SmartParking.Simulator/SmartParking.Simulator.csproj` |
| **Chạy Unit & Integration Test** | `test/SmartParking.Tests/` | `dotnet test --logger "console;verbosity=normal"` |
| **Build Firmware ESP32** | `IOT_SmartParking/` | `pio run` (hoặc `& "$env:USERPROFILE\.platformio\penv\Scripts\pio.exe" run`) |
| **Giao diện Web Trực quan** | `dashboard/` | Truy cập trực tiếp qua Backend: `http://localhost:5137` (hoặc `/index.html`, `/admin.html`, `/demo.html`) |

---

## 7. CÔNG CỤ GIẢ LẬP IOT (MQTT SIMULATOR) TRONG `tools/`

Công cụ CLI Console `SmartParking.Simulator` kết nối trực tiếp tới MQTT Broker (`broker.emqx.io:1883`) để kiểm thử toàn diện khả năng phản hồi và xử lý của Backend trước khi có phần cứng thật:

### 7.1 Cách chạy:
```powershell
# Chế độ tương tác tương tác (Interactive CLI):
dotnet run --project tools/SmartParking.Simulator/SmartParking.Simulator.csproj

# Hoặc chạy nhanh kịch bản tự động bằng đối số:
dotnet run --project tools/SmartParking.Simulator/SmartParking.Simulator.csproj -- scenario 1
dotnet run --project tools/SmartParking.Simulator/SmartParking.Simulator.csproj -- scenario 2
dotnet run --project tools/SmartParking.Simulator/SmartParking.Simulator.csproj -- scenario 3
dotnet run --project tools/SmartParking.Simulator/SmartParking.Simulator.csproj -- scenario 4
```

### 7.2 Các kịch bản kiểm thử có sẵn:
- **Kịch bản 1 (`scenario 1` hoặc `scenario normal`) - Xe vào bãi bình thường**:
  1. Xe gửi `PASS` qua cổng vào (`dacn-smartparking/lot1/gate/in/event`).
  2. Xe qua checkpoint nội bộ `CP1` (`dacn-smartparking/lot1/checkpoint/CP1/event`) -> Gia hạn thời gian vào ô.
  3. Cảm biến ô `E1` báo cự ly 28.5 cm (`dacn-smartparking/lot1/slot/E1/state`) -> Backend xác nhận xe đã đỗ (`PARKED`), LED chuyển đỏ.
- **Kịch bản 2 (`scenario 2` hoặc `scenario overlap`) - Xe đỗ lấn vạch (OVERLAP)**:
  1. Ô `E1` báo cự ly 65 cm (PARTIAL).
  2. Ô `E2` đồng thời báo cự ly 70 cm (PARTIAL).
  3. Backend phát hiện OVERLAP, bật đèn LED vàng cả 2 ô và phát tín hiệu cảnh báo.
- **Kịch bản 3 (`scenario 3` hoặc `scenario outage`) - Mất điện/đứt mạng và có điện lại**:
  1. Thiết bị gửi LWT `OFFLINE` -> Backend đánh dấu toàn bộ ô vật lý hàng E sang `UNKNOWN`.
  2. Thiết bị có điện lại gửi `ONLINE` kèm yêu cầu `resync` và `snapshot` cảm biến thực tế -> Backend audit và phục hồi ô đỗ.
- **Kịch bản 4 (`scenario 4` hoặc `scenario congestion`) - Bãi đông xe & Cổng Hold tự mở lại**:
  1. Giả lập số xe tìm chỗ đạt ngưỡng `MaxInLotSearching = 3` (Cổng vào HOLD).
  2. Một xe vào ô an toàn (`E1` -> 30 cm -> `PARKED`), số xe tìm chỗ giảm xuống 2 (< 3).
  3. Backend tự động phát lệnh `UNHOLD`/`OPEN` cho cổng vào.

---

## 8. BẢNG QUY ƯỚC TOPIC MQTT CHUẨN

| Topic | Publisher | Subscriber | QoS | Retain | Payload mẫu | Mô tả |
|---|---|---|:---:|:---:|---|---|
| `dacn-smartparking/lot1/slot/{id}/state` | ESP32 / Simulator | Backend | 1 | True | `{"spot":"E1","distanceCm":30.0}` | Dữ liệu cự ly cảm biến siêu âm từng ô đỗ |
| `dacn-smartparking/lot1/slot/{id}/led` | Backend | ESP32 | 1 | True | `"RED"`, `"GREEN"`, `"YELLOW"`, `"OFF"` | Điều khiển màu LED NeoPixel chỉ dẫn ô đỗ |
| `dacn-smartparking/lot1/gate/in/cmd` | Backend | ESP32 Barie | 1 | False | `{"cmd":"OPEN","barcode":"E123","plate":"51A-123.45"}` | Lệnh mở barie cổng vào hoặc tạm giữ (HOLD) |
| `dacn-smartparking/lot1/gate/in/event` | ESP32 Barie / Simulator | Backend | 1 | False | `PASS\|123` | Sự kiện xe đã đi qua thanh chắn cổng vào |
| `dacn-smartparking/lot1/gate/out/cmd` | Backend | ESP32 Barie | 1 | False | `{"cmd":"OPEN","orderId":123,"status":"COMPLETED"}` | Lệnh mở barie cổng ra |
| `dacn-smartparking/lot1/gate/out/event` | ESP32 Barie / Simulator | Backend | 1 | False | `LEAVE\|123` | Sự kiện xe đã đi qua thanh chắn cổng ra |
| `dacn-smartparking/lot1/checkpoint/{id}/event` | ESP32 / Simulator | Backend | 1 | False | `PASS\|123` | Cảm biến checkpoint phát hiện xe trên đường đi |
| `dacn-smartparking/lot1/device/{id}/status` | ESP32 (LWT) / Simulator | Backend | 1 | True | `"ONLINE"`, `"OFFLINE"` | Trạng thái nguồn / kết nối (MQTT Last Will) |
| `dacn-smartparking/lot1/resync` | ESP32 / Simulator | Backend | 1 | False | `{"action":"REQUEST_RESYNC"}` | Yêu cầu kiểm toán và đồng bộ lại dữ liệu sau sự cố |
| `dacn-smartparking/lot1/slots` | ESP32 / Simulator | Backend | 1 | True | `[{"spot":"E1","distanceCm":30.0,"state":"OCCUPIED"}]` | Snapshot cảm biến toàn bộ bãi đỗ |
| `dacn-smartparking/lot1/alert` | Backend | Dashboard / Monitor | 1 | False | `{"event":"OVERLAP","slot":"E1","adjacent":"E2"}` | Cảnh báo sự cố nghiệp vụ (Lấn vạch, đỗ chui, kẹt bãi) |

---

## 9. BẢNG ENDPOINT API MỚI (GIAI ĐOẠN 2B)

| Method | Endpoint | Quyền (Role) | Mô tả chức năng |
|:---:|---|:---:|---|
| `POST` | `/api/gate/in/scan` | Staff, Admin (hoặc Demo) | Check-in bước 1: Quét mã EntryCode + Mock ALPR (hỗ trợ nhập biển số hoặc truyền file ảnh `imageFileName`). Quét sai quá 3 lần bị khóa mã. Bãi đông sẽ phản hồi HOLD. Khách thường gọi trả 403 Forbidden. |
| `POST` | `/api/gate/in/pass` | MQTT / DemoMode | Check-in bước 2: Xe đi qua barie (sự kiện PASS). Đơn chuyển sang `ENTERED`, bắt đầu đếm ngược thời gian vào ô đỗ (`HanDoVaoO = 10m`). Ô chuyển `GUIDING`. Khách thường gọi trả 403 Forbidden. |
| `POST` | `/api/checkpoint/{id}/pass` | MQTT / DemoMode | Cảm biến xe qua trạm kiểm soát nội bộ. Tự động gia hạn thêm 5 phút tìm ô. Giới hạn tối đa 1 lần/checkpoint/đơn và không vượt quá `MaxTotalExtensionMin`. Khách thường gọi trả 403 Forbidden. |
| `POST` | `/api/bookings/{id}/report-traffic-jam` | Customer, Staff, Admin | Khách hàng bấm nút "Tôi đang kẹt": Tự động gia hạn thêm 5 phút tìm ô (tối đa 1 lần). |
| `POST` | `/api/gate/entry-code/{code}/unlock` | Staff, Admin | Nhân viên mở khóa mã vào cổng bị khóa do quét sai biển số quá số lần quy định, đặt lại số lần thử về 0. |
| `POST` | `/api/gate/out/scan` | Khách / Công khai | Quét mã / biển số ra cổng: Nếu xe ra khi chưa đỗ vào ô -> `ABANDONED`; nếu đỗ bình thường -> `COMPLETED`. Nếu quá giờ (`OVERSTAY`) -> Trả về cảnh báo yêu cầu nhân viên thu phí phạt. |
| `POST` | `/api/gate/out/overstay/confirm` | Staff, Admin | Nhân viên xác nhận thu tiền phạt quá giờ tại cổng ra, ghi nhận thanh toán `CASH_PENALTY`, mở cổng ra và hoàn tất đơn hàng. |
| `POST` | `/api/demo/virtual-slot/car-enter` | Demo (Công khai) | Mô phỏng sự kiện xe vào ô ảo (hàng A-D) khi ô đang `GUIDING`, đi qua cùng hàm xử lý trạng thái thống nhất như cảm biến thật, kích hoạt chuyển `PARKED` và tự mở cổng HOLD. |
| `POST` | `/api/demo/slot/state` | Demo (Công khai) | Đặt trạng thái ô bất kỳ (PARK, OVERLAP, FREE, LEAVE, MAINTENANCE) cho cả 32 ô ảo A-D và 8 ô vật lý E. |
| `GET` | `/hub/parking` | Tất cả | WebSocket SignalR Hub phát sự kiện thời gian thực: `SlotUpdated`, `OrderStatusUpdated`, `Alert`, `SummaryUpdated`, `TimeUpdated`, `BarrierUpdated`, `SystemLog`. |

---

## 10. TÀI LIỆU KỸ THUẬT PHẦN CỨNG & KIẾN TRÚC PHÂN TÁN
Hệ thống sử dụng **Kiến trúc phần cứng phân tán đa board (Multi-Board Distributed Architecture)** gồm **6 Vi điều khiển ESP32** kết nối hoàn toàn qua **WiFi / MQTT** (không dùng dây nối giữa các board):
- **1 Board Cổng (`esp32_gate`)**: Quản lý Barie vào/ra (2 servo PWM), cảm biến xe vào HC-SR04, 3 nút bấm (Gate Out, Checkpoint 1, Checkpoint 2) và màn hình LCD 20x4 I2C. Mô phỏng thực tế tại [wokwi/gate](wokwi/gate).
- **5 Board Hàng Ô (`esp32_row_a` .. `esp32_row_e`)**: Mỗi board quản lý độc lập 1 hàng 8 ô đỗ (8 cảm biến HC-SR04 dùng chung chân TRIG) và dải LED NeoPixel của hàng (đèn trạng thái ô + LED dẫn đường). Hàng E mô phỏng thực tế tại [wokwi/row_e](wokwi/row_e); các hàng A-D được giả lập linh hoạt bằng phần mềm qua [demo.html](dashboard/demo.html) và [tools/SmartParking.Simulator](tools/SmartParking.Simulator).
- Cùng chung mã nguồn firmware duy nhất (`src/main.cpp`), tùy biến vai trò qua cấu hình biên dịch PlatformIO (`platformio.ini`).

Danh mục tài liệu kỹ thuật chi tiết:
- [docs/kien-truc-phan-cung.md](docs/kien-truc-phan-cung.md): Báo cáo thiết kế kiến trúc phân tán, sơ đồ Mermaid, so sánh WiFi/MQTT với RS-485/CAN/LoRa/ESP-NOW, mạch đệm mức logic 5V/3.3V và các giới hạn kỹ thuật.
- [docs/pin-map.md](docs/pin-map.md): Bản đồ phân bổ chân GPIO cho Board Cổng (9 GPIO) và Board Hàng (10 GPIO), phân tích an toàn chân Strapping & JTAG.
- [docs/mqtt-topics.md](docs/mqtt-topics.md): Hợp đồng giao tiếp MQTT chuẩn hóa (bổ sung kênh `ack`, cảnh báo `alert` 2 chiều, định tuyến ánh xạ LED toàn cục sang cục bộ).
- [docs/wokwi-test.md](docs/wokwi-test.md): Hướng dẫn kiểm thử mô phỏng song song 2 board trên Wokwi, bảng đối ứng kịch bản và xử lý sự cố.
- [docs/circuit-isolation.md](docs/circuit-isolation.md): Sơ đồ cách ly chống nhiễu nguồn và xung điện.
- [docs/power-and-sleep.md](docs/power-and-sleep.md): Phân tích công suất nguồn và chế độ năng lượng.
