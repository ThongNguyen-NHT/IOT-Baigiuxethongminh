# BẢN ĐỒ KẾT NỐI CHÂN GPIO ESP32 (PIN MAP) - KIẾN TRÚC ĐA BOARD PHÂN TÁN

Tài liệu này quy định chi tiết phân bổ chân GPIO cho **6 vi điều khiển ESP32** trong kiến trúc phần cứng phân tán của hệ thống **IoT Smart Parking**.
Mọi board kết nối với nhau hoàn toàn qua **WiFi / MQTT**, không dùng bất kỳ đường dây tín hiệu cứng nào giữa các board.

---

## 1. NGUYÊN TẮC THIẾT KẾ & GIỚI HẠN CHÂN GPIO

1. **Nguyên tắc đóng gói theo chức năng (Role-based Encapsulation)**:
   - **Board Cổng (`esp32_gate`)**: Quản lý toàn bộ cơ cấu vào/ra (2 barie servo, 1 cảm biến xe vào HC-SR04, 3 nút sự kiện, màn hình LCD 20x4 I2C).
   - **5 Board Hàng Ô (`esp32_row_a` .. `esp32_row_e`)**: Mỗi board quản lý độc lập 1 hàng 8 ô đỗ (8 cảm biến siêu âm HC-SR04 và dải LED NeoPixel chỉ thị/dẫn đường riêng của hàng).
2. **Ngân sách GPIO cực kỳ an toàn (< 12 GPIO/board)**:
   - Board Cổng chỉ sử dụng **9 GPIO** (dư dả để mở rộng thêm còi báo buzzer hoặc RFID).
   - Mỗi Board Hàng chỉ sử dụng **10 GPIO** (dùng chung 1 chân TRIG cho 8 cảm biến siêu âm theo kỹ thuật TDM - Time Division Multiplexing).
3. **Ưu tiên chân Input-Only và tránh tuyệt đối Strapping nhạy cảm**:
   - Tận dụng tối đa các chân GPIO 34, 35, 36 (VP), 39 (VN) cho tín hiệu `ECHO` từ cảm biến siêu âm.
   - Không sử dụng các chân strapping nhạy cảm: `GPIO 0`, `GPIO 2`, `GPIO 5`, `GPIO 12`, `GPIO 15`.

---

## 2. BẢNG PHÂN BỔ CHÂN BOARD CỔNG (`esp32_gate`) - TỔNG CỘNG 9 GPIO

Board này được mô phỏng trong Wokwi tại thư mục `wokwi/gate/diagram.json` và biên dịch bằng profile `[env:gate]`.

| STT | Tên linh kiện trong diagram | Biến trong mã nguồn | Chân GPIO | Kiểu I/O | Chức năng phần cứng & Mô phỏng |
|:---:|---|---|:---:|:---:|---|
| **1** | `sensor_gate_in` (TRIG) | `PIN_TRIG_GATE_IN` | **GPIO 23** | Output | Phát xung kích 10µs cho cảm biến cự ly cổng vào |
| **2** | `sensor_gate_in` (ECHO) | `PIN_ECHO_GATE_IN` | **GPIO 34** | Input only | Đo khoảng cách phát hiện xe qua cổng vào |
| **3** | `barrier_in` (PWM) | `PIN_SERVO_GATE_IN` | **GPIO 4** | Output PWM | Động cơ Servo nâng/hạ barie cổng vào (0° = đóng, 90° = mở) |
| **4** | `barrier_out` (PWM) | `PIN_SERVO_GATE_OUT` | **GPIO 16** | Output PWM | Động cơ Servo nâng/hạ barie cổng ra (0° = đóng, 90° = mở) |
| **5** | `btn_gate_out` | `PIN_BTN_GATE_OUT` | **GPIO 18** | Input Pullup | Nút nhấn mô phỏng xe qua cổng ra (`leave`) |
| **6** | `btn_cp1` | `PIN_BTN_CP1` | **GPIO 19** | Input Pullup | Nút nhấn Checkpoint 1: Xác nhận xe qua ngã rẽ 1 |
| **7** | `btn_cp2` | `PIN_BTN_CP2` | **GPIO 32** | Input Pullup | Nút nhấn Checkpoint 2: Xác nhận xe qua ngã rẽ 2 |
| **8** | `lcd` (SDA) | `Wire (SDA)` | **GPIO 21** | I2C Data | Giao tiếp hiển thị LCD 20x4 địa chỉ `0x27` |
| **9** | `lcd` (SCL) | `Wire (SCL)` | **GPIO 22** | I2C Clock | Giao tiếp hiển thị LCD 20x4 địa chỉ `0x27` |

*Ghi chú*: Board Cổng **không có cảm biến ô** và **không có LED NeoPixel**.

---

## 3. BẢNG PHÂN BỔ CHÂN BOARD HÀNG Ô (`esp32_row_a` .. `esp32_row_e`) - TỔNG CỘNG 10 GPIO

Áp dụng đồng nhất cho cả 5 board hàng. Riêng hàng E được mô phỏng trong Wokwi tại `wokwi/row_e/diagram.json` (biên dịch `[env:row_e]`). Các hàng A, B, C, D biên dịch tương ứng với `[env:row_a]` .. `[env:row_d]`.

| STT | Tên linh kiện trong diagram | Biến trong mã nguồn | Chân GPIO | Kiểu I/O | Chức năng phần cứng & Mô phỏng |
|:---:|---|---|:---:|:---:|---|
| **1** | Cả 8 HC-SR04 của hàng (TRIG)| `PIN_TRIG_ALL` | **GPIO 23** | Output | Phát xung kích 10µs chung cho cả 8 cảm biến siêu âm |
| **2** | `sensor_x1` (ECHO) | `SLOT_ECHO_PINS[0]` | **GPIO 35** | Input only | Đo khoảng cách ô đỗ thứ 1 của hàng |
| **3** | `sensor_x2` (ECHO) | `SLOT_ECHO_PINS[1]` | **GPIO 36** (VP) | Input only | Đo khoảng cách ô đỗ thứ 2 của hàng |
| **4** | `sensor_x3` (ECHO) | `SLOT_ECHO_PINS[2]` | **GPIO 39** (VN) | Input only | Đo khoảng cách ô đỗ thứ 3 của hàng |
| **5** | `sensor_x4` (ECHO) | `SLOT_ECHO_PINS[3]` | **GPIO 13** | Input | Đo khoảng cách ô đỗ thứ 4 của hàng (JTAG TCK an toàn) |
| **6** | `sensor_x5` (ECHO) | `SLOT_ECHO_PINS[4]` | **GPIO 14** | Input | Đo khoảng cách ô đỗ thứ 5 của hàng (JTAG TMS an toàn) |
| **7** | `sensor_x6` (ECHO) | `SLOT_ECHO_PINS[5]` | **GPIO 25** | Input | Đo khoảng cách ô đỗ thứ 6 của hàng |
| **8** | `sensor_x7` (ECHO) | `SLOT_ECHO_PINS[6]` | **GPIO 26** | Input | Đo khoảng cách ô đỗ thứ 7 của hàng |
| **9** | `sensor_x8` (ECHO) | `SLOT_ECHO_PINS[7]` | **GPIO 27** | Input | Đo khoảng cách ô đỗ thứ 8 của hàng |
| **10**| `pixels` (DIN) | `PIN_NEOPIXEL` | **GPIO 33** | Output | Điều khiển dải LED WS2812B của hàng (đèn ô & dẫn đường) |

*Ghi chú*: Board Hàng **không có servo, LCD hay nút nhấn**.

---

## 4. PHÂN TÍCH AN TOÀN CHÂN STRAPPING & JTAG

Mọi thiết kế chân trên cả 2 loại board đều tuân thủ nguyên tắc an toàn phần cứng:

### A. Các chân Strapping nhạy cảm - ĐÃ TRÁNH HOÀN TOÀN:
- **GPIO 0**: Strapping bootloader pin. $\rightarrow$ Để trống.
- **GPIO 2**: Strapping boot / LED onboard. $\rightarrow$ Để trống.
- **GPIO 5**: Strapping SPI flash CS timing. $\rightarrow$ Để trống.
- **GPIO 12 (MTDI)**: Strapping điện áp Flash 1.8V/3.3V (rất nguy hiểm gây brick flash boot-loop). $\rightarrow$ **Tránh hoàn toàn, không kết nối.**
- **GPIO 15 (MTDO)**: Strapping debug log output. $\rightarrow$ Để trống.

### B. Các chân JTAG / Đa năng được sử dụng và lý do an toàn:
1. **GPIO 13 & GPIO 14 (nối chân ECHO của cảm biến ô 4 và 5 trên board Hàng)**:
   - Mặc định là JTAG TCK và TMS. Khi không bật JTAG phần cứng ngoài, ESP32 sử dụng 2 chân này như GPIO thông thường.
   - Khi ở trạng thái nghỉ, chân ECHO của HC-SR04 luôn ở mức 0V (GND), không có trở kéo cao (pull-up) cưỡng bức nên không gây cản trở quá trình boot của chip.
2. **GPIO 34, 35, 36, 39 (Input-Only)**:
   - Thuần túy là chân đọc analog/digital input, không có mạch đệm output và không có trở kéo nội. Cực kỳ ổn định cho việc đo độ rộng xung phản hồi ECHO.
3. **GPIO 4 & 16 (PWM Servo trên Board Cổng)**:
   - Là chân xuất số chuẩn, điều khiển xung 50Hz cho servo mượt mà, không can thiệp vào thanh ghi strapping.

---

## 5. LƯU Ý KỸ THUẬT PHẦN CỨNG THẬT (HARDWARE IMPLEMENTATION)

> [!WARNING]
> **TƯƠNG THÍCH ĐIỆN ÁP 5V CỦA HC-SR04 VÀ ESP32 3.3V:**
> 1. Cảm biến HC-SR04 chuẩn hoạt động ở `VCC = 5V`. Xung `ECHO` phát ra có biên độ **5V**.
> 2. Các chân GPIO của ESP32 chỉ chịu được tối đa **3.3V - 3.6V**. Cắm trực tiếp 5V lâu dài sẽ làm cháy cổng I/O của vi điều khiển.
> 3. **Giải pháp thực tế**:
>    - **Dùng cầu phân áp**: Tại mỗi chân ECHO, mắc nối tiếp trở $1k\Omega$ tới GPIO ESP32 và trở $2k\Omega$ từ GPIO xuống GND ($V_{out} = 5V \times \frac{2k}{1k + 2k} \approx 3.33V$).
>    - **Dùng IC đệm mức logic**: Dùng IC TXS0108E (8 kênh 5V $\leftrightarrow$ 3.3V) cho 8 chân ECHO của mỗi board hàng.
>    - **Hoặc dùng cảm biến RCWL-9610 / HC-SR04P**: Hoạt động nguyên bản từ nguồn 3.3V, không cần mạch hạ áp.
> 4. **Mô phỏng Wokwi**: Wokwi cho phép nối thẳng chân logic mà không làm cháy linh kiện ảo, giúp sơ đồ `diagram.json` giữ được độ tinh gọn tối đa.
