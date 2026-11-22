# TỔNG HỢP 20 CÂU HỎI PHẢN BIỆN BẢO VỆ ĐỒ ÁN VÀ ĐÁP ÁN KỸ THUẬT

> **Tài liệu**: 20 câu hỏi thường gặp nhất từ Hội đồng phản biện kèm câu trả lời ngắn gọn, chuẩn xác dựa trên mã nguồn thực tế của dự án Smart Parking IoT.

---

### Câu 1: Vì sao chọn giao thức MQTT cho ESP32 thay vì HTTP REST thông thường?
**Đáp án**:  
1. **Tiết kiệm tài nguyên**: Header của MQTT chỉ từ $2 \text{ bytes}$, trong khi HTTP Header tốn hàng trăm bytes, phù hợp vi điều khiển ESP32 giới hạn RAM.  
2. **Hai chiều thời gian thực (Push model)**: Backend có thể gửi lệnh điều khiển barie và đèn LED xuống ESP32 ngay lập tức ($< 50\text{ms}$) mà ESP32 không phải chạy vòng lặp thăm dò (polling) gây quá nhiệt và tốn băng thông.  
3. **Cơ chế LWT (Last Will)**: Giúp máy chủ phát hiện thiết bị mất kết nối ngay lập tức mà HTTP không có.

---

### Câu 2: Vì sao chọn mạng không dây WiFi giữa các board mà không dùng dây truyền thông RS-485 hay CAN bus?
**Đáp án**:  
Trong bãi đỗ xe diện tích lớn, kéo dây RS-485/CAN đòi hỏi đục cắt sàn bê tông, rủi ro đứt ngầm khi xe tải trọng lớn cán qua rất cao và chi phí lắp đặt đắt đỏ. Sử dụng WiFi giúp mỗi hàng ô chỉ cần một đường cấp nguồn độc lập $5\text{V}$, các board kết nối hoàn toàn phân tán (Decoupled), dễ dàng lắp thêm hàng ô mới mà không ảnh hưởng hệ thống cũ.

---

### Câu 3: Nếu bãi xe bị mất điện một phần (ví dụ chỉ hàng E mất nguồn), hệ thống xử lý ra sao?
**Đáp án**:  
Ngay khi board hàng E mất điện, Broker MQTT lập tức phát bản tin LWT (`device/esp32_row_e/status` = `"OFFLINE"`). Backend nhận bản tin này trong `MqttBackgroundService.HandleDeviceStatusAsync` và **chỉ chuyển 8 ô thuộc hàng E sang trạng thái UNKNOWN**, các hàng A-D vẫn hoạt động bình thường. Khi có điện lại, board gửi `"ONLINE"`, Backend tự động Audit và phục hồi màu đèn LED chuẩn xác.

---

### Câu 4: Cơ chế chống đặt trùng ô (Double Booking) hoạt động như thế nào ở mức Database?
**Đáp án**:  
Hệ thống kết hợp 2 lớp phòng vệ:  
1. **Khóa giao dịch (Row Lock / Concurrency Control)** trong EF Core: Khi 2 khách cùng bấm giữ ô, chỉ 1 transaction giành được quyền ghi và chuyển ô sang `HELD`, transaction còn lại bị từ chối ngay (`Conflict 409`).  
2. **Kiểm tra khoảng đệm an toàn**: Ràng buộc khung giờ hẹn phải cách nhau tối thiểu `MinGapBetweenBookingsMin = 30 phút` để tránh xe trước chưa kịp ra xe sau đã tới.

---

### Câu 5: Ô ảo (Virtual Slots hàng A-D) là gì? Tại sao lại dùng ô ảo trong đồ án?
**Đáp án**:  
Ô ảo là các ô đỗ trên phần mềm được mô phỏng đầy đủ dữ liệu (tọa độ hàng/cột, trạng thái, đơn đặt, dẫn đường), nhưng không gắn cảm biến vật lý HC-SR04 thật mà được gán board giả lập. Việc này giúp đồ án chứng minh được khả năng quản lý quy mô lớn (40 ô, 5 hàng) với chi phí phần cứng tối ưu, trong khi mã nguồn firmware PlatformIO đã sẵn sàng chia 6 môi trường (`gate`, `row_a` .. `row_e`) để nạp vào board thật bất cứ lúc nào.

---

### Câu 6: Làm thế nào hệ thống phát hiện được xe đỗ lấn vạch (OVERLAP)?
**Đáp án**:  
Hệ thống sử dụng thuật toán đối soát cự ly trên 2 ô kề nhau: Nếu 2 cảm biến siêu âm của 2 ô liền kề (như E1 và E2) cùng đo được khoảng cách lơ lửng trong dải $40\text{ cm} \le d \le 80\text{ cm}$, Backend xác định xe đang đỗ chèn lên vạch sơn ngăn cách, lập tức đổi trạng thái cả 2 ô sang `OVERLAP`, phát lệnh MQTT chớp đèn LED vàng và cảnh báo nhân viên trực.

---

### Câu 7: Khác biệt giữa bản tin Retained và Non-retained trong hệ thống là gì?
**Đáp án**:  
- **Retained = true**: Dành cho **trạng thái tĩnh bền vững** (`slot/{id}/state`, `device/{id}/status`, `summary`). Broker lưu lại bản tin mới nhất; khi có subscriber mới kết nối, Broker phát ngay trạng thái hiện tại.  
- **Retained = false**: Dành cho **lệnh một lần** (`gate/in/cmd`, `guide/cmd`, `alert`, `slot/{id}/cmd`). Tránh việc khi ESP32 khởi động lại bị Broker phát lại lệnh cũ gây nhảy barie hoặc sáng đèn sai.

---

### Câu 8: Tại sao lại bỏ cờ Retained đối với lệnh điều khiển đèn LED ô (`slot/{id}/cmd`)?
**Đáp án**:  
Trước đây khi để retained cho `slot/E*/cmd`, nếu trên Broker còn lưu tin cũ (ví dụ lệnh `"RED"` của ô E8 từ phiên trước), khi ESP32 vừa bật nguồn, Broker đẩy ngay tin cũ xuống làm ô E8 sáng đỏ dù ô đang trống. Giải pháp đúng là bỏ retained; thay vào đó khi board kết nối lại (`ONLINE`), Backend sẽ tự động đọc Database và gửi lại màu đèn chuẩn xác theo nghiệp vụ hiện tại.

---

### Câu 9: Cơ chế Last Will and Testament (LWT) hoạt động ra sao trong firmware ESP32?
**Đáp án**:  
Khi gọi `mqttClient.connect(deviceId, ..., willTopic, willQos, willRetain, willPayload)`, ESP32 ký gửi sẵn cho Broker một bản tin di chúc (`"OFFLINE"`). Trong suốt quá trình hoạt động, ESP32 gửi heartbeat định kỳ. Nếu Broker không nhận được tín hiệu trong khoảng thời gian keepalive (do mất điện, đứt cáp), chính Broker sẽ thay mặt ESP32 phát bản tin `"OFFLINE"` tới Backend.

---

### Câu 10: Tại sao lại dùng bảng mã Crockford cho mã tiền mặt CashCode?
**Đáp án**:  
Mã CashCode tiền mặt 6 ký tự sử dụng bảng mã Crockford Base32 loại bỏ các ký tự dễ gây nhầm lẫn khi đọc bằng mắt hoặc đọc qua điện thoại: loại bỏ chữ `I`, `L` (dễ nhầm với số `1`), chữ `O` (dễ nhầm với số `0`), chữ `U` (tránh tạo thành từ tục tĩu). Giúp nhân viên và khách hàng đối soát bằng miệng nhanh chóng, không sai sót.

---

### Câu 11: Nếu khách quét mã mở barie vào cổng rồi bỏ đi không lái xe vào thì sao?
**Đáp án**:  
Barie có cơ chế an toàn: Barie chỉ mở trong 10 giây. Đơn hàng chỉ chuyển sang `ENTERED` khi cảm biến hồng ngoại tại cổng gửi sự kiện `PASS` (xác nhận xe đã cắt qua chùm tia). Nếu sau 10 giây xe không đi qua, barie tự động đóng lại; Backend ghi log cảnh báo và mã quét được giữ nguyên hoặc hủy tùy cấu hình.

---

### Câu 12: Khi bãi đông xe hoặc kẹt đường nội bộ, hệ thống hỗ trợ thế nào?
**Đáp án**:  
1. **Gate HOLD**: Khi số xe đang tìm chỗ đạt ngưỡng `MaxInLotSearching = 5`, cổng vào tạm thời không cho xe mới vào để tránh vỡ trận.  
2. **Nút "Tôi đang kẹt xe" & Checkpoint CP1/CP2**: Khách hàng bấm nút trên Web PWA hoặc bấm nút tại cột trạm phụ để được tự động cộng thêm 5 phút di chuyển (tối đa 1 lần), không bị phạt hủy đơn.

---

### Câu 13: Khách hàng đến trễ sau giờ hẹn thì hệ thống xử lý thế nào? Tiền có được hoàn lại không?
**Đáp án**:  
Hệ thống cho phép khung giờ check-in $[T-15', T+15']$. Nếu quá $T+15$ phút khách không quét mã vào cổng, đơn chuyển sang `NO_SHOW` và ô được giải phóng cho người khác. **Số tiền thanh toán không được hoàn lại** (Non-refundable) nhằm bảo vệ quyền lợi bãi xe và chấm dứt tình trạng đặt chỗ ảo giữ ô không đến.

---

### Câu 14: Thuật toán tính tiền phạt quá giờ (OVERSTAY) tính như thế nào?
**Đáp án**:  
Khi xe đỗ vượt quá gói thời lượng đã mua (ví dụ gói 1 giờ nhưng đỗ 75 phút):  
$$\text{Tiền phạt} = \text{Số phút quá hạn} \times 200 \text{ VNĐ/phút}$$
Xe ra cổng bị barie chặn lại. Nhân viên trực bàn `staff.html` thu đúng số tiền phạt hiển thị trên hệ thống và bấm xác nhận, barie ra mới tự động nâng lên cho xe qua.

---

### Câu 15: Khi Broker MQTT bị mất mạng rồi có lại, Backend làm sao để không spam kết nối?
**Đáp án**:  
Backend áp dụng thuật toán **Exponential Backoff Reconnect**: Lần đầu thử lại sau $1\text{s}$, nếu thất bại tăng lên $2\text{s} \rightarrow 5\text{s} \rightarrow 10\text{s} \rightarrow 30\text{s}$ (tối đa 30s). Giúp giảm tải áp lực mạng và tránh hiện tượng dồn dập (thundering herd). Ngay khi kết nối thành công, Backend tự động subscribe lại đủ 6 topic.

---

### Câu 16: Cùng lúc cả 6 board ESP32 gửi dữ liệu về thì Broker có bị nghẽn không?
**Đáp án**:  
Không nghẽn, vì:  
1. Tần suất gửi cự ly của mỗi cảm biến đã được kiểm soát (chỉ gửi khi khoảng cách thay đổi $> 5\text{ cm}$ và cách nhau tối thiểu $300\text{ms}$).  
2. Bản tin snapshot định kỳ được giãn cách tối thiểu $5\text{ giây}$.  
3. Broker MQTT xử lý bất đồng bộ hàng chục nghìn gói tin mỗi giây trên giao thức TCP nhẹ.

---

### Câu 17: Dải LED NeoPixel dẫn đường hoạt động thế nào khi chia thành nhiều board?
**Đáp án**:  
Backend gửi một danh sách số thứ tự pixel toàn cục (ví dụ `[0, 1, 10, 11, 16]`). Trong bảng `ThietBi`, mỗi board được gán sẵn `PixelOffset` và `PixelCount` (ví dụ board E có offset 10, count 8 quản lý pixel 10-17). Mỗi board chỉ lọc và điều khiển các pixel nằm trong phạm vi của mình, tạo thành hiệu ứng dải đèn dẫn đường liền mạch xuyên suốt toàn bãi mà không cần nối dây LED giữa các board.

---

### Câu 18: Tranh chấp đồng thời giữa Webhook thanh toán và tiến trình Timeout Worker giải quyết ra sao?
**Đáp án**:  
Sử dụng kỹ thuật **Optimistic Concurrency Control** thông qua cột phiên bản dòng `RowVersion` (Timestamp byte[]) của SQL Server. Nếu Worker và Webhook cùng cập nhật một đơn hàng tại cùng một phần nghìn giây, transaction nào đến sau sẽ kiểm tra trạng thái: Nếu đơn đã thành `PAID` thì Worker hủy bỏ lệnh hủy `EXPIRED`, đảm bảo khách hàng tuyệt đối không bị mất tiền oan.

---

### Câu 19: Tại sao dùng SignalR cho giao diện Web thay vì gọi Ajax Polling mỗi 2 giây?
**Đáp án**:  
Polling liên tục mỗi 2 giây sẽ gây ra hàng nghìn HTTP request vô ích khi bãi xe không có biến động, làm quá tải CPU máy chủ. SignalR thiết lập kết nối song công (Duplex WebSocket), máy chủ chỉ đẩy dữ liệu xuống client khi có sự kiện thực tế phát sinh (xe vào, đổi trạng thái ô, phát cảnh báo), giảm $95\%$ lưu lượng mạng và độ trễ gần như bằng 0.

---

### Câu 20: Những hạn chế lớn nhất của hệ thống và giải pháp nâng cấp khi làm thương mại là gì?
**Đáp án**:  
1. **Camera AI ANPR**: Hiện mô hình đang mô phỏng nhận diện biển số qua giao diện web; khi thương mại hóa sẽ tích hợp Camera IP AI nhận dạng biển số tự động trong 0.2s.  
2. **Cổng thanh toán thực**: Hiện dùng mô phỏng webhook ngân hàng; thương mại hóa sẽ ký hợp đồng tích hợp cổng VNPay/MoMo/VietQR thật.  
3. **Cụm Broker nội bộ**: Chuyển từ HiveMQ Cloud sang cụm EMQX Cluster on-premise có mã hóa SSL/TLS và chứng chỉ bảo mật riêng.
