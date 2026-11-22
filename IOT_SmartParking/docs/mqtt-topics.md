# TÀI LIỆU HỢP ĐỒNG GIAO TIẾP MQTT (MQTT TOPIC SPECIFICATION)

> **Dự án**: Hệ thống Quản lý Bãi đỗ xe Thông minh (Smart Parking IoT)  
> **Mục đích**: Tài liệu hợp đồng giao tiếp chuẩn hóa duy nhất giữa **Backend (.NET 8)**, **Dashboard** và **Firmware ESP32 (Wokwi / Hardware)** cho Giai đoạn 3.  
> **Mặc định Topic Prefix**: `dacn-smartparking/lot1` (Cấu hình linh hoạt qua `ParkingPolicy:MqttTopicPrefix`).

---

## 1. BẢNG TỔNG HỢP DANH MỤC TOPIC MQTT

| STT | Topic | Hướng truyền (Pub -> Sub) | QoS | Retain | Payload JSON / Chuỗi mẫu | Ý nghĩa & Mô tả nghiệp vụ |
|:---:|---|---|:---:|:---:|---|---|
| **1** | `{prefix}/slot/{id}/state` | ESP32 -> Server | 1 | True | `{"spot":"E1","distanceCm":28.5}` | Cảm biến siêu âm gửi cự ly xe đo được tại ô đỗ. $\le 40$cm: `OCCUPIED`, $40-80$cm: `PARTIAL` (kiểm tra `OVERLAP`), $>80$cm: `FREE`. |
| **2** | `{prefix}/slot/{id}/cmd` | Server -> ESP32 | 1 | False | `"RED"` / `"GREEN"` / `"YELLOW"` / `"OFF"` | Lệnh đổi màu đèn LED chỉ dẫn trạng thái tại từng ô đỗ (`RED`: có xe, `GREEN`: dẫn đường/trống, `YELLOW`: lấn vạch, `OFF`: tắt). Không giữ retained; khi board kết nối lại/resync Backend sẽ tự động phát lại trạng thái đèn tương ứng. |
| **3** | `{prefix}/guide/cmd` | Server -> ESP32 | 1 | False | `{"slot":"E1","path":[0,1,2,3],"color":"GREEN","ttl":120}` | Lệnh sáng dải LED NeoPixel dẫn đường từ cổng vào ô chỉ định (dữ liệu pixel lấy từ bảng `DuongDanDen`, tự tắt sau `ttl` giây). |
| **4** | `{prefix}/display/cmd` | Server -> ESP32 | 1 | False | `{"line1":"CHAO MUNG","line2":"O DO: E1"}` | Lệnh hiển thị nội dung hướng dẫn trên màn hình LCD 1602 / 2004 tại cổng vào hoặc ngã rẽ. |
| **5** | `{prefix}/gate/in/cmd` | Server -> ESP32 Barie | 1 | False | `{"cmd":"OPEN","barcode":"E123","transitSec":60}` | Lệnh mở thanh chắn barie cổng vào khi quét hợp lệ, hoặc lệnh `HOLD` (bãi đông) / `UNHOLD` (tự mở lại khi thông thoáng). |
| **6** | `{prefix}/gate/in/event` | ESP32 Barie -> Server | 1 | False | `{"type":"pass","eventId":"evt_in_101"}` | Cảm biến từ / beam barie báo xe **đã đi qua cổng vào**. Cảm biến không giữ mã đơn, Server tự ghép với đơn vừa quét mở cổng. |
| **7** | `{prefix}/gate/out/cmd` | Server -> ESP32 Barie | 1 | False | `{"cmd":"OPEN","status":"COMPLETED"}` | Lệnh mở thanh chắn barie cổng ra cho xe rời khỏi bãi sau khi xác thực hợp lệ hoặc hoàn tất nộp phạt quá giờ. |
| **8** | `{prefix}/gate/out/event` | ESP32 Barie -> Server | 1 | False | `{"type":"leave","eventId":"evt_out_101"}` | Cảm biến từ / beam barie báo xe **đã rời khỏi bãi đỗ xe**. Server tự ghép và hoàn tất đơn hàng. |
| **9** | `{prefix}/checkpoint/{id}/event` | ESP32 -> Server | 1 | False | `{"type":"pass","eventId":"evt_cp1_01"}` | Cảm biến chùm tia tại trạm kiểm soát nội bộ ngã rẽ phát hiện xe. Server tự động gia hạn thêm 5 phút tìm ô cho xe đang tìm chỗ. |
| **10** | `{prefix}/device/{id}/status` | ESP32 -> Server | 1 | True | `"ONLINE"` hoặc `"OFFLINE"` | Trạng thái sống/kết nối của thiết bị ESP32. Sử dụng cơ chế **MQTT Last Will and Testament (LWT)** để báo mất điện / rớt mạng. |
| **11** | `{prefix}/device/{id}/power` | ESP32 -> Server | 1 | True | `{"batteryVolt":3.7,"powerSource":"MAIN"}` | Giám sát nguồn cấp thực tế của node IoT (Retained: Server hoặc Dashboard kết nối sau đọc được ngay mức pin/nguồn). |
| **12** | `{prefix}/resync` | ESP32 <-> Server | 1 | False | `{"action":"REQUEST_RESYNC"}` | Thiết bị gửi yêu cầu đồng bộ lại toàn bộ trạng thái sau khi khởi động lại / online trở lại sau sự cố mất điện. |
| **13** | `{prefix}/summary` | Server -> Dashboard | 0 | True | `{"total":40,"free":32,"occupied":7,"overlap":1}` | Thống kê tổng hợp thời gian thực của toàn bộ bãi đỗ xe phục vụ màn hình giám sát và bảng điện tử ngoài cổng. |
| **14** | `{prefix}/alert` | Backend & ESP32 -> Backend & Dashboard/Staff | 1 | False | `{"type":"UNAUTHORIZED_ENTRY",...}` hoặc `{"event":"OVERLAP",...}` | Kênh cảnh báo sự cố 2 chiều: ESP32 cảnh báo vi phạm vật lý (`UNAUTHORIZED_ENTRY`); Backend cảnh báo đỗ lấn vạch (`OVERLAP`), đỗ chui (`UNAUTHORIZED_PARKING`), mất điện (`DEVICE_OFFLINE`). |
| **15** | `{prefix}/ack` | Server -> ESP32/Sim | 1 | False | `{"eventId":"evt_in_101"}` | Phản hồi xác nhận đã nhận & xử lý sự kiện mang eventId từ cổng hoặc checkpoint. Firmware nhận được sẽ gỡ sự kiện khỏi hàng đợi RAM để ngừng retry. |

---

## 2. CHI TIẾT CẤU TRÚC PAYLOAD TỪNG NHÓM TOPIC

### 2.1 Cảm biến cự ly ô đỗ (`slot/{id}/state`)
- **Topic**: `dacn-smartparking/lot1/slot/E1/state`
- **Publisher**: ESP32 (đọc từ cảm biến siêu âm HC-SR04)
- **QoS**: 1, **Retain**: true
- **Payload**:
```json
{
  "spot": "E1",
  "distanceCm": 28.5
}
```

### 2.2 Điều khiển đèn LED ô đỗ (`slot/{id}/cmd`)
- **Topic**: `dacn-smartparking/lot1/slot/E1/cmd`
- **Publisher**: Backend API
- **QoS**: 1, **Retain**: false (KHÔNG dùng retained; khi node kết nối lại hoặc resync, Backend chủ động gửi lại trạng thái đèn chuẩn)
- **Payload**: `"RED"` (Có xe), `"GREEN"` (Ô trống / Dẫn đường), `"YELLOW"` (Đỗ lấn vạch), `"OFF"` (Tắt)

### 2.3 Điều khiển dải LED dẫn đường NeoPixel (`guide/cmd`)
- **Topic**: `dacn-smartparking/lot1/guide/cmd`
- **Publisher**: Backend API (sinh ra ngay khi nhận sự kiện `PASS` cổng vào)
- **Subscriber**: Tất cả các board hàng (`esp32_row_a` .. `esp32_row_e`)
- **QoS**: 1, **Retain**: false
- **Payload**:
```json
{
  "slot": "E7",
  "path": [0, 1, 2, 3, 4, 5, 6, 16],
  "color": "GREEN",
  "ttl": 120
}
```
- **Cơ chế ánh xạ Toàn cục $\rightarrow$ Cục bộ (Global-to-Local Mapping)**:
  - Backend gửi danh sách chỉ số pixel toàn cục (ví dụ pixel $0..49$ theo bảng `DuongDanDen`).
  - Mỗi board hàng (`row_*`) có hằng số cấu hình biên dịch: `PIXEL_OFFSET` và `PIXEL_COUNT`.
  - Khi nhận `guide/cmd`, board duyệt qua mảng `path`. Nếu một chỉ số pixel $P$ thỏa mãn:
    $$PIXEL\_OFFSET \le P < PIXEL\_OFFSET + PIXEL\_COUNT$$
    thì board sẽ bật LED cục bộ tại vị trí index: $P_{local} = P - PIXEL\_OFFSET$.
  - Các pixel không nằm trong dải của board sẽ được bỏ qua một cách an toàn. Nhờ đó, backend chỉ cần phát 1 thông điệp broadcast duy nhất cho toàn bãi đỗ.

### 2.4 Màn hình hiển thị cổng vào (`display/cmd`)
- **Topic**: `dacn-smartparking/lot1/display/cmd`
- **Publisher**: Backend API
- **QoS**: 1, **Retain**: false
- **Payload**:
```json
{
  "line1": "XIN CHAO 51A-12345",
  "line2": "VAO O DO: E7"
}
```

### 2.5 Cổng vào - Lệnh mở / Tạm giữ barie (`gate/in/cmd`)
- **Topic**: `dacn-smartparking/lot1/gate/in/cmd`
- **Publisher**: Backend API
- **QoS**: 1, **Retain**: false
- **Payload mở cổng**:
```json
{
  "cmd": "OPEN",
  "barcode": "ENTRY123456",
  "plate": "51A-12345",
  "spot": "E1",
  "transitSec": 60
}
```
- **Payload tạm giữ do bãi đông (HOLD)**:
```json
{
  "cmd": "HOLD",
  "reason": "CONGESTION",
  "searchingCount": 3,
  "maxAllowed": 3,
  "message": "Bãi đang đông xe tìm chỗ, barie tạm giữ đóng."
}
```
- **Payload tự động mở lại khi bãi thông thoáng (UNHOLD)**:
```json
{
  "cmd": "OPEN",
  "reason": "UNHOLD",
  "searchingCount": 2,
  "message": "Bãi xe đã thông thoáng, cổng vào tiếp tục hoạt động."
}
```

### 2.6 Cổng vào - Sự kiện xe qua barie (`gate/in/event`)
- **Topic**: `dacn-smartparking/lot1/gate/in/event`
- **Publisher**: ESP32 Barie (Cảm biến chùm tia hoặc công tắc hành trình)
- **QoS**: 1, **Retain**: false
- **Quy ước**: Cảm biến không biết mã đơn của khách hàng, chỉ phát tín hiệu xe đã qua.
- **Payload**:
```json
{
  "type": "pass",
  "eventId": "in_evt_20261121_001"
}
```

### 2.7 Cổng ra - Sự kiện xe qua barie (`gate/out/event`)
- **Topic**: `dacn-smartparking/lot1/gate/out/event`
- **Publisher**: ESP32 Barie cổng ra
- **QoS**: 1, **Retain**: false
- **Payload**:
```json
{
  "type": "leave",
  "eventId": "out_evt_20261121_001"
}
```

### 2.8 Trạm kiểm soát nội bộ bãi xe (`checkpoint/{id}/event`)
- **Topic**: `dacn-smartparking/lot1/checkpoint/CP1/event`
- **Publisher**: ESP32 Checkpoint
- **QoS**: 1, **Retain**: false
- **Payload**:
```json
{
  "type": "pass",
  "eventId": "cp_evt_001"
}
```

### 2.9 Quản lý trạng thái nguồn & mất kết nối LWT (`device/{id}/status`)
- **Topic**: `dacn-smartparking/lot1/device/{deviceId}/status`
- **Publisher**: Các node ESP32 (cấu hình Last Will and Testament trên MQTT Client khi kết nối)
- **Danh sách Device ID trong hệ thống**:
  - `esp32_gate`: Node điều khiển cổng và trạm kiểm soát
  - `esp32_row_a`: Node điều khiển hàng A (ô A1 - A8)
  - `esp32_row_b`: Node điều khiển hàng B (ô B1 - B8)
  - `esp32_row_c`: Node điều khiển hàng C (ô C1 - C8)
  - `esp32_row_d`: Node điều khiển hàng D (ô D1 - D8)
  - `esp32_row_e`: Node điều khiển hàng E (ô E1 - E8)
- **QoS**: 1, **Retain**: true
- **Payload**: `"ONLINE"` khi khởi động kết nối bình thường; `"OFFLINE"` do broker tự động gửi khi node mất nguồn/rớt sóng đột ngột.
- **Tác động nghiệp vụ Backend**:
  - Khi nhận `OFFLINE` từ `esp32_row_X`: Chỉ các ô thuộc hàng $X$ mới bị chuyển sang trạng thái cảnh báo `UNKNOWN`. Các hàng khác vẫn hoạt động độc lập bình thường.
  - Khi nhận `ONLINE` trở lại từ `esp32_row_X`: Backend tự động gửi yêu cầu `resync` đến riêng node đó để lấy snapshot cự ly và đối soát lại đơn đỗ.

### 2.10 Xác nhận xử lý sự kiện độ tin cậy cao (`ack`)
- **Topic**: `dacn-smartparking/lot1/ack`
- **Publisher**: Backend API (.NET 8)
- **Subscriber**: ESP32 Firmware / Simulator
- **QoS**: 1, **Retain**: false
- **Payload mẫu**:
```json
{
  "eventId": "in_evt_1732200000_123"
}
```
- **Thời điểm gửi**: Ngay sau khi Backend nhận được và xử lý thành công một sự kiện mang `eventId` từ các kênh `gate/in/event`, `gate/out/event`, hoặc `checkpoint/{id}/event`.
- **Cơ chế phía Firmware**: Firmware lưu các sự kiện quan trọng trong hàng đợi RAM. Nếu sau 2 giây chưa nhận được gói tin ACK tương ứng từ Backend, Firmware sẽ tự động gửi lại (retry) tối đa 3 lần với cùng `eventId` trước khi hủy gói tin để tránh nghẽn bộ nhớ. Khi nhận được ACK, Firmware lập tức loại bỏ sự kiện khỏi hàng đợi.

### 2.11 Cảnh báo sự cố & Vi phạm (`alert`)
- **Topic**: `dacn-smartparking/lot1/alert`
- **Publisher**: **Backend API** và **ESP32 Firmware**
- **Subscriber**: **Backend API** (lắng nghe cảnh báo từ ESP32 để lưu `NhatKyHeThong` và phát SignalR realtime), **Dashboard Quản trị** (`admin.html`) và **Dashboard Nhân viên** (`staff.html`).
- **QoS**: 1, **Retain**: false

#### A. Payload mẫu do ESP32 phát (Cảnh báo phần cứng & An ninh):
Phát khi cảm biến cổng vào phát hiện xe tiếp cận barie trong khi barie đang ở trạng thái **ĐÓNG** (`gateInOpen == false`):
```json
{
  "type": "UNAUTHORIZED_ENTRY",
  "message": "Phat hien xe tiep can khi cong vao dang dong",
  "ts": "1234567"
}
```

#### B. Payload mẫu do Backend API phát (Cảnh báo nghiệp vụ hệ thống):
Phát khi phát hiện đỗ lấn vạch (`OVERLAP`), xe đỗ chui không có đơn hợp lệ (`UNAUTHORIZED_PARKING`), hoặc thiết bị mất kết nối (`DEVICE_OFFLINE`):
```json
{
  "source": "BACKEND",
  "event": "OVERLAP",
  "slot": "E1",
  "adjacent": "E2",
  "distanceCm": 55.0,
  "message": "Phát hiện xe đỗ lấn vạch giữa ô E1 và E2"
}
```
*(Cờ `"source":"BACKEND"` giúp Backend tự nhận biết và bỏ qua việc ghi lặp nhật ký khi nhận lại chính thông điệp do mình phát đi).*

---

## 3. NGUYÊN TẮC CỜ RETAINED & CÔNG CỤ DỌN DẸP BROKER

### 3.1 Nguyên tắc phân định cờ Retained
- **ĐƯỢC phép Retain (Chỉ dành cho trạng thái bền vững)**:
  - `{prefix}/slot/{id}/state`: Cự ly cảm biến đo được gần nhất của từng ô đỗ.
  - `{prefix}/device/{id}/status`: Trạng thái kết nối LWT (`ONLINE` / `OFFLINE`).
  - `{prefix}/device/{id}/power`: Thông số nguồn cấp, điện áp pin thực tế của thiết bị.
  - `{prefix}/summary`: Số liệu thống kê tổng hợp bãi xe phục vụ màn hình ngoài cổng.
- **TUYỆT ĐỐI KHÔNG Retain (Lệnh điều khiển & Sự kiện một lần)**:
  - Lệnh điều khiển: `gate/{in|out}/cmd`, `guide/cmd`, `display/cmd`, `device/{id}/cmd`, `slot/{id}/cmd`.
  - Nếu `slot/{id}/cmd` giữ retained, khi thiết bị khởi động lại sẽ vô tình đọc phải lệnh màu cũ từ phiên trước gây sai lệch hiển thị. Do đó, khi node IoT kết nối lại (`ONLINE`) hoặc có yêu cầu `resync`, Backend sẽ tự động phát lại màu LED hiện tại dựa trên dữ liệu database thật.
  - Sự kiện & phản hồi: `gate/+/event`, `checkpoint/+/event`, `ack`, `alert`.

### 3.2 Công cụ dọn dẹp tin Retained cũ (`tools/clean_retained_mqtt.py`)
Khi đổi hợp đồng hoặc sau các đợt chạy thử nghiệm, Broker có thể vẫn lưu các tin retained cũ của các topic lệnh. Để dọn dẹp triệt để:
1. **Dọn các topic lệnh không hợp lệ** (tự động giữ lại các topic trạng thái hợp lệ):
   ```powershell
   python tools/clean_retained_mqtt.py --broker broker.hivemq.com --prefix dacn-smartparking/lot1
   ```
2. **Xóa toàn bộ tin Retained** (khi muốn đưa broker về trạng thái trắng tinh):
   ```powershell
   python tools/clean_retained_mqtt.py --broker broker.hivemq.com --prefix dacn-smartparking/lot1 --all
   ```
*Cơ chế kỹ thuật*: Script sẽ subscribe `{prefix}/#`, thu thập mọi topic có `retain=True`, sau đó publish một payload rỗng (`len = 0`) với cờ `retain=True` lên topic đó để Broker xóa vĩnh viễn khỏi bộ nhớ theo chuẩn MQTT v3.1.1/v5.0.
