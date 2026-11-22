# HƯỚNG DẪN KIỂM THỬ MÔ PHỎNG ĐA BOARD TRÊN WOKWI (DUAL-BOARD WOKWI SIMULATION)

Tài liệu này hướng dẫn chi tiết cách biên dịch và chạy song song **2 board ESP32 thật trên Wokwi** (`gate` và `row_e`) cùng với 4 hàng ảo (`row_a` .. `row_d`) được giả lập qua phần mềm, khớp với hợp đồng giao tiếp MQTT tại [docs/mqtt-topics.md](file:///E:/IOT/IOTCODE/IOT_SmartParking/docs/mqtt-topics.md).

---

## 1. CẤU TRÚC MÔ HÌNH VÀ THƯ MỤC WOKWI

Kiến trúc phân tán gồm 6 vi điều khiển ESP32 giao tiếp hoàn toàn qua **WiFi / MQTT**:
- **Board 1 (`wokwi/gate`)**: Quản lý Barie vào/ra (2 servo), cảm biến xe vào HC-SR04, 3 nút sự kiện (Gate Out, CP1, CP2) và LCD 20x4 I2C.
- **Board 2 (`wokwi/row_e`)**: Quản lý 8 cảm biến siêu âm HC-SR04 cho ô `E1` - `E8` và dải LED NeoPixel của hàng E.
- **Boards 3-6 (Hàng A, B, C, D)**: Đã có sẵn firmware chuẩn mực (`[env:row_a]` .. `[env:row_d]`), trong môi trường thử nghiệm được mô phỏng linh hoạt bởi [demo.html](file:///E:/IOT/IOTCODE/IOT_SmartParking/dashboard/demo.html) và công cụ [SmartParking.Simulator](file:///E:/IOT/IOTCODE/IOT_SmartParking/tools/SmartParking.Simulator/Program.cs).

Thư mục lưu trữ:
```text
IOT_SmartParking/
├── wokwi/
│   ├── gate/
│   │   ├── diagram.json    # Sơ đồ phần cứng Board Cổng
│   │   └── wokwi.toml      # Trỏ tới .pio/build/gate/firmware.bin
│   └── row_e/
│       ├── diagram.json    # Sơ đồ phần cứng Board Hàng E
│       └── wokwi.toml      # Trỏ tới .pio/build/row_e/firmware.bin
```

---

## 2. CÁC BƯỚC BIÊN DỊCH VÀ KHỞI CHẠY MÔ PHỎNG

### Bước 1: Biên dịch toàn bộ Firmware bằng PlatformIO và chép file nhị phân
Mở PowerShell tại thư mục gốc dự án và chạy:
```powershell
cd E:\IOT\IOTCODE\IOT_SmartParking
& "$env:USERPROFILE\.platformio\penv\Scripts\pio.exe" run

# Chép firmware trực tiếp vào thư mục mô phỏng của từng board:
Copy-Item ".pio\build\gate\firmware.bin" "wokwi\gate\firmware.bin" -Force
Copy-Item ".pio\build\gate\firmware.elf" "wokwi\gate\firmware.elf" -Force
Copy-Item ".pio\build\row_e\firmware.bin" "wokwi\row_e\firmware.bin" -Force
Copy-Item ".pio\build\row_e\firmware.elf" "wokwi\row_e\firmware.elf" -Force
```
*PlatformIO sẽ biên dịch cả 6 môi trường (`gate`, `row_e`, `row_a`, `row_b`, `row_c`, `row_d`). File nhị phân được đặt ngay trong thư mục `wokwi/gate/` và `wokwi/row_e/` để Wokwi simulator nhận diện trực tiếp.*

### Bước 2: Khởi động Backend API (.NET 8)
```powershell
cd E:\IOT\IOTCODE\IOT_SmartParking\backend
dotnet run --project SmartParking.Api.csproj
```
Backend lắng nghe tại `http://localhost:5137` và kết nối MQTT broker `broker.hivemq.com:1883`, prefix `dacn-smartparking/lot1`.

### Bước 3: Chạy đồng thời 2 Board trên Wokwi

#### Cách A: Chạy trong 2 cửa sổ VS Code riêng biệt (Khuyên dùng để test đồng thời)
1. Cửa sổ VS Code 1: Mở file [wokwi/gate/diagram.json](file:///E:/IOT/IOTCODE/IOT_SmartParking/wokwi/gate/diagram.json) $\rightarrow$ Bấm nút **Play** (hoặc `F1` $\rightarrow$ `Wokwi: Start Simulator`). Board `esp32_gate` sẽ khởi động, kết nối WiFi/MQTT, hiển thị LCD chào mừng.
2. Cửa sổ VS Code 2: Mở file [wokwi/row_e/diagram.json](file:///E:/IOT/IOTCODE/IOT_SmartParking/wokwi/row_e/diagram.json) $\rightarrow$ Bấm nút **Play**. Board `esp32_row_e` sẽ khởi động, kết nối MQTT và tự động công bố (snapshot) trạng thái ban đầu của 8 ô hàng E.

#### Cách B: Chạy kiểm thử từng board theo kịch bản
- Khi muốn kiểm tra cổng vào/ra, LCD, nút bấm: Chạy [wokwi/gate/diagram.json](file:///E:/IOT/IOTCODE/IOT_SmartParking/wokwi/gate/diagram.json).
- Khi muốn kiểm tra cự ly đỗ xe ô E1-E8, LED NeoPixel dẫn đường: Chạy [wokwi/row_e/diagram.json](file:///E:/IOT/IOTCODE/IOT_SmartParking/wokwi/row_e/diagram.json).

---

## 3. BẢNG KỊCH BẢN THAO TÁC LIÊN BOARD (END-TO-END SCENARIO MATRIX)

| STT | Kịch bản kiểm thử | Thao tác trên Wokwi | Tương ứng trên Web | Kết quả phản hồi thời gian thực |
|:---:|---|---|---|---|
| **1** | **Mở cổng $\rightarrow$ Xe vào qua barie (PASS)** | **Wokwi Gate**: Đợi servo mở $\rightarrow$ Kéo slider `sensor_gate_in` xuống `30 cm` trong 2s, rồi kéo về `150 cm`. | Trên [demo.html](file:///E:/IOT/IOTCODE/IOT_SmartParking/dashboard/demo.html) mục *Mô phỏng cổng vào*: Nhập mã đơn đã PAID, bấm **Quét mã vào**. | - Wokwi Gate: Servo `barrier_in` mở 90° $\rightarrow$ xe qua $\rightarrow$ tự đóng lại.<br>- `customer.html`: Đơn đổi sang `ENTERED`, đếm ngược 10 phút vào ô.<br>- `admin.html`: Ô đỗ chuyển sang `GUIDING`. |
| **2** | **Dẫn đường NeoPixel liên board** | **Wokwi Row E**: Quan sát dải NeoPixel khi xe qua cổng. | Trên `customer.html`: Đơn đang `ENTERED`, bấm **"Bật dải LED dẫn đường"**. | - Backend gửi `guide/cmd` với mảng pixel toàn cục `[0,1,2,10]`.<br>- Wokwi Row E nhận lệnh, tự ánh xạ pixel 10 $\rightarrow$ LED cục bộ số 0 sáng xanh lá (`GREEN`). |
| **3** | **Xe vào đỗ đúng ô (OCCUPIED)** | **Wokwi Row E**: Kéo slider `sensor_e1` xuống `30 cm` (giữ 2s debounce). | Kịch bản 1 & 7 trên `demo.html`. | - Wokwi Row E: LED ô E1 đổi sang màu **ĐỎ** (`RED`), LED dẫn đường tắt.<br>- `customer.html`: Đơn chuyển sang `PARKED`.<br>- `admin.html`: Ô E1 đổi màu đỏ `OCCUPIED`. |
| **4** | **Xe đỗ lấn vạch 2 ô (OVERLAP)** | **Wokwi Row E**: Kéo slider `sensor_e1` về `60 cm` và `sensor_e2` về `65 cm` (cả 2 trong khoảng 40-80cm, giữ 2s). | Kịch bản 8 trên `demo.html`. | - Wokwi Row E: LED của cả E1 và E2 chuyển sang màu **VÀNG** (`YELLOW`).<br>- `admin.html` / `staff.html`: Chuông cảnh báo kêu, hiển thị sự cố `OVERLAP`. |
| **5** | **Gia hạn thời gian qua Checkpoint** | **Wokwi Gate**: Bấm nút nhấn `btn_cp1` (hoặc `btn_cp2`). | Kịch bản 6 trên `demo.html`. | - Wokwi Gate phát `checkpoint/CP1/event`.<br>- Backend phản hồi `ack`.<br>- `customer.html`: Thời gian vào ô được cộng thêm **+5 phút**. |
| **6** | **Xe rời bãi qua Cổng ra (LEAVE)** | **Wokwi Gate**: Bấm nút nhấn `btn_gate_out`. | Kịch bản 10 trên `demo.html`. | - Wokwi Gate: Servo `barrier_out` quay mở 90° trong 3 giây rồi đóng.<br>- Backend hoàn tất đơn đỗ xe (`COMPLETED`). |
| **7** | **Mất điện Board Hàng E & Đối soát đỗ chui** | 1. **Wokwi Row E**: Bấm **Stop Simulation** (ngắt nguồn).<br>2. Kéo slider `sensor_e4` xuống `30 cm` (mô phỏng xe lạ chiếm ô).<br>3. Bấm **Play** (cấp điện lại). | Mục *Mô phỏng nguồn điện* trên `demo.html` chọn `esp32_row_e`. | - **Khi mất điện**: Broker gửi LWT `OFFLINE`, **chỉ 8 ô hàng E chuyển sang `UNKNOWN`**, các hàng A-D không bị ảnh hưởng.<br>- **Khi có điện lại**: ESP32 gửi snapshot $\rightarrow$ Backend phát hiện ô E4 có xe nhưng không có đơn $\rightarrow$ Tạo cảnh báo **`UNAUTHORIZED_PARKING`** (WARNING), phục hồi 7 ô còn lại về `FREE`. |

---

## 4. BẢNG XỬ LÝ SỰ CỐ: NẾU KHÔNG THẤY X THÌ KIỂM TRA Y

| Hiện tượng gặp phải (X) | Nguyên nhân khả dĩ | Hướng dẫn kiểm tra & Khắc phục (Y) |
|---|---|---|
| **Wokwi báo lỗi không tìm thấy `firmware.bin`** | Chưa biên dịch môi trường tương ứng | Chạy `pio run -e gate` hoặc `pio run -e row_e` để PlatformIO build file nhị phân vào đúng thư mục `.pio/build/<env>/`. |
| **Kéo `sensor_gate_in` nhưng không phát sinh `pass`** | Cổng vào chưa được lệnh mở | Firmware cổng vào chỉ phát `pass` khi barie đang ở trạng thái **MỞ (OPEN)** trong thời hạn `transitSec = 60s`. Hãy quét mã vào trên `demo.html` trước. |
| **Kéo slider cự ly ô nhưng Web không cập nhật** | Chưa đủ thời gian lọc nhiễu 2s | Thuật toán trong `src/main.cpp` yêu cầu cự ly ổn định liên tục qua ít nhất 2 chu kỳ đọc (mỗi chu kỳ cách nhau 1 giây). Cần giữ slider ở vị trí mới tối thiểu 2 giây. |
| **Mất điện hàng E làm ảnh hưởng cả bãi?** | Nhầm lẫn Device ID | Kiểm tra xem backend đã nhận đúng topic `device/esp32_row_e/status` hay chưa. Với kiến trúc mới, sự cố của board nào chỉ cô lập và ảnh hưởng đến hàng ô do board đó quản lý. |
| **Không thấy đèn LED dẫn đường của hàng E sáng** | Chỉ số pixel không nằm trong dải hàng E | Hàng E quản lý LED từ offset 10 đến 17 (`PIXEL_OFFSET=10, PIXEL_COUNT=8`). Hãy chắc chắn đơn đặt xe đang dẫn vào ô thuộc hàng E (`E1` - `E8`). |
