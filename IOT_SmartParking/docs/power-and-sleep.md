# QUẢN LÝ NGUỒN VÀ TRẠNG THÁI HOẠT ĐỘNG (POWER MANAGEMENT)

Tài liệu này giải thích cơ chế cấp nguồn và quản lý trạng thái online/offline của node ESP32 trong hệ thống **IoT Smart Parking**.

---

## 1. NGUỒN ĐIỆN VÀ TÍNH TOÁN PIN DỰ PHÒNG

Hệ thống hoạt động với nguồn điện chính 5V DC (qua cổng USB hoặc chân VIN của ESP32) cấp cho vi điều khiển, cảm biến HC-SR04, động cơ servo và màn hình LCD.

### Công thức tính công suất và dung lượng pin dự phòng:
Với mỗi tải, ghi điện áp $V$ và dòng tiêu thụ $I$. Công suất trung bình:

```text
P(W) = V(V) * I(A)
I_pin(A) = P_total(W) / (V_pin(V) * hiệu_suất_bộ_nguồn)
Dung lượng tối thiểu (Ah) = I_pin(A) * thời_gian_hoạt_động(h) * hệ_số_dự_phòng
```
- Khuyến nghị sử dụng hệ số dự phòng `1.3` và hiệu suất bộ hạ áp `0.85`.
- Khi khởi động, node ESP32 gửi thông điệp kiểm tra nguồn lên topic `dacn-smartparking/lot1/device/{deviceId}/power`:
  ```json
  {"batteryVolt": 3.7, "powerSource": "MAIN"}
  ```

---

## 2. KHÔNG DÙNG DEEP SLEEP (HOẠT ĐỘNG LIÊN TỤC)

1. **Lý do không sử dụng Deep Sleep**:
   - ESP32 đảm nhiệm việc quét liên tục 9 cảm biến siêu âm (8 ô hàng E và cảm biến cổng vào Gate In) để phát hiện xe kịp thời.
   - ESP32 phải duy trì kết nối WiFi và MQTT để nhận các lệnh điều khiển mở cổng barie (`gate/in/cmd`, `gate/out/cmd`), dải đèn LED chỉ dẫn lối đi (`guide/cmd`) và cập nhật màn hình LCD (`display/cmd`) thời gian thực.
   - Do đó, vi điều khiển hoạt động liên tục ở chế độ Active Mode, không vào chế độ Deep Sleep.

2. **Mô phỏng mất điện / phục hồi nguồn trong demo.html**:
   - Nút bấm **"Mô phỏng mất điện hàng E" / "Cấp điện lại"** trên trang `demo.html` là cơ chế mô phỏng ở tầng mạng/MQTT:
     - Khi bấm **Mất điện**: Backend / Broker kích hoạt sự kiện Last Will and Testament (LWT) `device/esp32_lot1/status = "OFFLINE"`, đánh dấu 8 ô hàng E chuyển sang `UNKNOWN`.
     - Khi bấm **Cấp điện lại**: Node IoT online trở lại (`device/esp32_lot1/status = "ONLINE"`), tự động gửi tín hiệu `resync` và snapshot trạng thái cảm biến để Server đối soát phần cứng vs CSDL (Audit phát hiện xe đỗ chui).
   - Trong môi trường Wokwi, việc ngắt nguồn được mô phỏng bằng cách nhấn nút **Stop Simulation** (dừng chạy mạch) hoặc ngắt kết nối WiFi.
