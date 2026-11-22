# BÁO CÁO TIẾN ĐỘ THỰC HIỆN DỰ ÁN (SMART PARKING IOT)

> **Cập nhật thời gian thực**: 22/11/2026  
> **Nguyên tắc báo cáo**: Ghi nhận kết quả thực nghiệm từ các lệnh build, test và công cụ kiểm tra tự động thật của hệ thống.

---

## 1. KẾT QUẢ THỰC HIỆN NHÓM 1: ỔN ĐỊNH VÀ DỌN DẸP

### 1.1 Công việc đã hoàn thành
1. **MQTT Retained Optimization**:
   - Rà soát toàn bộ backend và firmware: Chỉ giữ `retain=true` cho các topic trạng thái (`slot/{id}/state`, `device/{id}/status`, `device/{id}/power`, `summary`).
   - Loại bỏ cờ retain đối với toàn bộ các lệnh một lần (`gate/{in|out}/cmd`, `guide/cmd`, `display/cmd`, `device/{id}/cmd`, `ack`, `alert`, `slot/{id}/cmd`).
   - Bổ sung cơ chế: Khi board ESP32 kết nối lại (`ONLINE`) hoặc có yêu cầu `resync`, Backend tự động truy vấn DB và gửi lại màu đèn LED chuẩn (`GREEN`, `RED`, `YELLOW`, `BLUE`) cho toàn bộ các ô thuộc hàng đó.
   - Viết công cụ dọn dẹp tin retained cũ: [tools/clean_retained_mqtt.py](file:///E:/IOT/IOTCODE/IOT_SmartParking/tools/clean_retained_mqtt.py). Đã chạy thực tế xóa sạch 8 topic `slot/E*/cmd` retained cũ trên Broker `broker.hivemq.com`.
   - Cập nhật đầy đủ hợp đồng tại [docs/mqtt-topics.md](file:///E:/IOT/IOTCODE/IOT_SmartParking/docs/mqtt-topics.md).
2. **Backend Reconnect MQTT với Exponential Backoff**:
   - Thay thế vòng lặp cố định 2 giây bằng thuật toán Exponential Backoff tăng dần: $1s \rightarrow 2s \rightarrow 5s \rightarrow 10s \rightarrow 30s$ (tối đa 30s).
   - Log cảnh báo súc tích kèm số lần thử và thời gian chờ, tránh spam log console.
   - Phương thức `SubscribeTopicsAsync()` được đảm bảo tự động kích hoạt sau mỗi lần kết nối lại thành công, subscribe đủ 6 topic.
3. **Phục hồi trạng thái RAM OVERLAP & Snapshot/Resync**:
   - Trạng thái `_recentSlotReadings` trong RAM được bổ sung phương thức phục hồi tự động từ database `RestoreAndRecalculateOverlapFromDbAsync`.
   - Khi Backend khởi động lại hoặc khi nhận bản tin `resync` / `device/+/status ONLINE`, Backend tự động nạp lại cự ly các ô từ SQL Server và tính toán lại các cặp ô lấn vạch (`OVERLAP`), phát cảnh báo và điều khiển LED vàng kịp thời.
4. **Dọn dẹp code & bảng thừa (Dead Code Removal)**:
   - Đã xóa bỏ các entity/service di sản không còn dùng: `MqttReliableEvent`, `MqttEventInbox`, `ReliableEventService`, `ParkingSyncService`, `SnapshotAuditService`, `ReliableEventDto`, `SmartParkingDbContext.Sync.cs`.
   - Đã gỡ bỏ toàn bộ các switch-case cũ (`reliable/event`, `sessions`, `slots`, `event`) trong `MqttBackgroundService.cs`.
   - Cập nhật script SQL [QL_BAIGIUXE_IOT.sql](file:///E:/IOT/IOTCODE/IOT_SmartParking/QL_BAIGIUXE_IOT.sql) và [QL_BAIGIUXE_IOT_TEST.sql](file:///E:/IOT/IOTCODE/IOT_SmartParking/QL_BAIGIUXE_IOT_TEST.sql), thực thi xóa bảng `MqttReliableEvent` an toàn và giữ script chạy lại nhiều lần (idempotent).
5. **Kiểm thử liên hoàn 12 kịch bản**:
   - Chạy 12 kịch bản demo liên tiếp **3 vòng lặp (36 lượt kịch bản) mà không cần reset dữ liệu giữa chừng**. Tỷ lệ đạt: **100% (Passed)**.
6. **Bảng đối chiếu 16 tình huống nghiệp vụ và Unit Test tự động**:
   - Bổ sung 6 test mới (`Test27` đến `Test32`), nâng tổng số test lên **32 tests**.

| STT | Tình huống nghiệp vụ | Hiện tượng / Rủi ro nếu không xử lý | Tên Unit Test tự động (test/) | Kịch bản Demo tương ứng |
|:---:|:---|:---|:---|:---:|
| 1 | **Đặt trùng ô** | Hai khách cùng đặt 1 ô tại cùng một thời điểm gây xung đột | `Test1_HaiKhachGiuCungMotOCungLuc_TaskWhenAll_ChiMotNguoiThanhCong`<br>`Test16_TichHopSqlServerThat_HaiDbContextSongSong_Lap20Lan_KhongDatTrung` | Kịch bản 1 (`scenario 1`) |
| 2 | **Đặt chồng khung giờ** | Khách đặt ô trùng giờ hoặc vi phạm khoảng đệm an toàn `MinGapBetweenBookingsMin` | `Test4_DatChongKhungGioVaKhoangDemMinGap_BiTuChoi` | Kiểm tra đặt chỗ |
| 3 | **Hết hạn thanh toán** | Giữ chỗ (HELD) nhưng quá 5 phút không thanh toán | `Test6_DonKhongThanhToanSau5Phut_ExpiredVaNhaO` | Kịch bản 2 (`scenario 2`) |
| 4 | **Đến muộn (NO_SHOW)** | Đã thanh toán nhưng quá 15 phút giờ hẹn không đến quét mã | `Test7_QuaHanCheckIn15Phut_NoShowKhongHoanTien`<br>`Test14_DonANoShowNhungDonBGiuCungOKhungGioSau_OKhongBiNhaFreeSai` | Kịch bản 3 (`scenario 3`) |
| 5 | **Quét mã không vào cổng** | Mở barie 60s nhưng xe bỏ đi, không qua chùm tia | `Test17_CheckInHaiBuoc_QuetDung_MoCong_QuetSaiVuotQuaLimit_BiKhoa` | Kịch bản 4 (`scenario 4`) |
| 6 | **Vào bãi rồi quay ra ngay** | Xe vào cổng (ENTERED) nhưng lập tức ra cổng mà không vào ô | `Test18_XeRaKhiChuaDo_ChuyenSangAbandoned_NhaODo` | Kịch bản 5 (`scenario 5`) |
| 7 | **Bãi đông (Hold & Auto-unhold)** | Bãi đông xe tìm ô (`MaxInLotSearching`), tự động đóng và mở lại cổng | `Test19_BaiDongXeDangTimCho_MaxInLotSearching_TuChoiMoCong`<br>`Test25_BaiDong_Hold_KhiXeTimGiamXuong_CongTuMoLai_VaXeChoKhongBiTinhHanDo` | Kịch bản 6 (`scenario 6`) |
| 8 | **Checkpoint gia hạn** | Kẹt xe đường nội bộ, không gia hạn lặp tại cùng 1 checkpoint | `Test20_NutToiDangKet_VaCheckpoint_GiaHanThoiGianVaoO`<br>`Test24_Checkpoint_LapLaiCungCheckpoint_KhongGiaHanThem` | Kịch bản 6 (`scenario 6`) |
| 9 | **Hết hạn đi vào ô (Đổi ô / FreeExit)** | Xe trong bãi quá 10 phút chưa đỗ, tự đổi ô trống khác hoặc FreeExit | `Test21_HetHanVaoOMaChuaDo_TuDoiOSangFree_NeuHetChoThiFreeExit` | Kịch bản 7 (`scenario 7`) |
| 10 | **Đỗ lấn vạch (OVERLAP)** | Xe đỗ chèn vạch 2 ô liền kề (cự ly 40-80cm) | `Test22_OverlapBangKhoangCach_PartitalVaAdjacent_PhatHienLanVach`<br>`Test30_Overlap_KhoiPhucTuDatabase_SauKhiKhoiDongLai` | Kịch bản 8 (`scenario 8`) |
| 11 | **Mất điện từng board** | LWT OFFLINE board nào chỉ đánh dấu `UNKNOWN` hàng đó | `Test23_MatDienDutMangLWT_DanhDauUnknown_VaAuditXeDoChui`<br>`Test31_MatDienTungBoard_ChiAnhHuongODoThuocHangDo` | Kịch bản 9 (`scenario 9`) |
| 12 | **Quá giờ đỗ xe (OVERSTAY)** | Xe gửi quá thời gian đã mua, tính phạt 200đ/phút | `Test27_XeQuaGioDat_Overstay_ChanCongRa_ThuPhatMoiMo` | Kịch bản 10 (`scenario 10`) |
| 13 | **Thanh toán trùng / Idempotency** | Webhook ngân hàng gửi lặp lại hoặc đồng thời với timeout worker | `Test8_WebhookNganHangThat_KiemTraChuKyHmacSoTienVaIdempotent`<br>`Test13_ThanhToanVaWorkerChayDongThoi_ChiDuyNhatMotTrangThaiCuoiCung_KhongMatTien` | Kịch bản 11 (`scenario 11`) |
| 14 | **Bảo mật cổng (Chặn PASS giả)** | Khách/thiết bị tự gửi sự kiện PASS khi cổng chưa mở | `Test28_BaoMatCong_TuChoiPassKhiChuaCoLenhMoCong` | Bảo mật barie |
| 15 | **Nối lại MQTT & Subscribe lại** | Broker mất mạng và nối lại với backoff, tự subscribe lại toàn bộ topics | `Test29_MqttReconnect_VaResubscribeAllTopics` | Khả năng tự phục hồi |
| 16 | **Kiểm thử liên hoàn 12 kịch bản** | Chạy 12 kịch bản 3 lần liên tiếp không reset | `Test32_Chay12KichBanLienTiep3Lan_KhongReset_ThanhCong100PhanTram` | Toàn bộ 12 kịch bản |

### 1.2 Kết quả thực thi thật từ hệ thống (Nhóm 1)
- **`dotnet build backend/SmartParking.Api.csproj`**:
  `Build succeeded. 0 Warning(s), 0 Error(s). Time Elapsed 00:00:03.92`
- **`dotnet test test/SmartParking.Tests/SmartParking.Tests.csproj`**:
  `Passed! - Failed: 0, Passed: 32, Skipped: 0, Total: 32, Duration: 5 s`
- **`pio run` (Toàn bộ 6 environments)**:
  - `env:gate`: SUCCESS (00:00:31.281)
  - `env:row_e`: SUCCESS (00:00:28.315)
  - `env:row_a`: SUCCESS (00:00:18.818)
  - `env:row_b`: SUCCESS (00:00:16.356)
  - `env:row_c`: SUCCESS (00:00:17.739)
  - `env:row_d`: SUCCESS (00:00:18.077)
  - **Tổng kết PlatformIO**: `6 succeeded in 00:02:10.585`.


---

## 2. KẾT QUẢ THỰC HIỆN NHÓM 2: HOÀN THIỆN SẢN PHẨM

### 2.1 Chi tiết công việc hoàn thành
1. **admin.html (Thống kê trực quan & Xuất báo cáo)**:
   - Tích hợp 3 biểu đồ SVG tự vẽ (thuần SVG/JavaScript chuẩn, 0 thư viện bên ngoài):
     + **Doanh thu theo ngày**: SVG Bar Chart các cột ngày kèm số tiền (VNĐ) và số lượt gửi xe.
     + **Tỷ lệ lấp đầy**: SVG Donut Chart hiển thị % lấp đầy, số ô trống, ô đã đặt, ô có xe.
     + **Số đơn theo trạng thái**: SVG Progress Bars trực quan tỉ lệ các trạng thái đơn hàng (`COMPLETED`, `PARKED`, `PAID`, `PENDING_PAYMENT`, `OVERSTAY`, `NO_SHOW`, ...).
   - Bảng **Nhật ký hệ thống**: Bổ sung bộ lọc cấp độ (`Tất cả`, `INFO`, `WARNING`, `ERROR`) và thời gian (từ ngày / đến ngày).
   - Nút **"Xuất CSV doanh thu"**: Gọi trực tiếp `GET /api/thong-ke/xuat-csv-doanh-thu`, tải về file CSV mã hóa UTF-8 BOM hiển thị chuẩn tiếng Việt có dấu trong Excel.
   - Quản lý **6 Board ESP32 (Task 11)**: Hiển thị trạng thái kết nối `ONLINE`/`OFFLINE`, vai trò (`GATE`, `ROW A-E`), dải LED phụ trách, các ô quản lý và thời điểm cập nhật cuối cùng đọc trực tiếp từ bảng `ThietBi`.
2. **customer.html (Trải nghiệm khách hàng & Chính sách minh bạch)**:
   - Hiển thị tab **Lịch sử đơn đặt chỗ** với nhãn trạng thái tiếng Việt chuẩn mực, chi tiết cước phí và khung giờ.
   - Bổ sung khung **Quy định & Chính sách bãi xe** nổi bật:
     + Hạn thanh toán: 5 phút tự hủy và nhả ô.
     + Khung giờ check-in: $T-15$ đến $T+15$ phút; quá hạn tính `NO_SHOW`.
     + Chính sách không hoàn tiền khi `NO_SHOW`.
     + Thời gian vào ô: 10 phút sau khi qua cổng vào; hỗ trợ nút "Tôi đang kẹt xe" gia hạn thêm 5 phút.
     + Phí phạt quá hạn (`OVERSTAY`): 200 VNĐ / phút gửi quá giờ, barie cổng ra tạm khóa chờ xử lý.
   - **Thông báo trong trang (Toast)**:
     + Tự động bật cảnh báo toast khi đồng hồ đếm ngược còn dưới 2 phút ($< 120s$).
     + Tự động bật thông báo toast khi trạng thái đơn hàng thay đổi qua SignalR (`OrderStatusUpdated`, `BookingStatusChanged`).
3. **staff.html (Bàn trực nhân viên phân cấp ưu tiên)**:
   - Thiết lập **Bảng việc cần xử lý gấp (Priority Action Center)** xếp theo 4 cấp độ ưu tiên nghiệp vụ:
     + **Ưu tiên 1 (Khẩn cấp - Đỏ)**: `OVERSTAY` - Các xe quá hạn đỗ, hiển thị số phút quá hạn, tính phạt 200đ/phút, nút "Thu phạt & Mở cổng ra".
     + **Ưu tiên 2 (Cao - Cam)**: `Hết ô / Kẹt xe nội bộ` - Các xe qua cổng vào quá 10 phút chưa vào ô, nút "Đổi ô trống khác" và "Cho ra miễn phí (FreeExit)".
     + **Ưu tiên 3 (Trung bình - Vàng)**: `Mã vào cổng bị khóa (LOCKED)` - Quét sai quá 3 lần, nút "Mở khóa ngay".
     + **Ưu tiên 4 (Theo dõi - Xanh/Tím)**: `Cảnh báo Realtime` - Cảnh báo đỗ lấn vạch (`OVERLAP`), đỗ chui (`UNAUTHORIZED_PARKING`), mất điện LWT.
4. **Chuẩn hóa giao diện & Responsive 360px (Toàn hệ thống)**:
   - Cả 4 trang (`admin.html`, `customer.html`, `staff.html`, `demo.html`) đều hiển thị huy hiệu SignalR 3 trạng thái (`Đã kết nối`, `Đang nối lại...`, `Mất kết nối`) và nút **"Đồng bộ lại"**.
   - Kiểm tra và tinh chỉnh CSS responsive hoàn hảo trên màn hình điện thoại từ 360px trở lên.
   - Toàn bộ giao diện sử dụng tiếng Việt rõ ràng, nhãn trạng thái nghiệp vụ thân thiện, không còn chuỗi lỗi tiếng Anh thô.

### 2.2 Kết quả xác minh Nhóm 2
- `dotnet build backend/SmartParking.Api.csproj`: **Build succeeded (0 warning, 0 error)**.
- `dotnet test test/SmartParking.Tests/SmartParking.Tests.csproj`: **32/32 tests PASSED (0 failed)**.
- `pio run` (tất cả 6 môi trường: `gate`, `row_e`, `row_a`, `row_b`, `row_c`, `row_d`): **6 succeeded in 42.56s**.
- Đã chép firmware mới nhất (`firmware.bin`, `firmware.elf`) vào thư mục `wokwi/gate/` và `wokwi/row_e/`.


---

## 3. KẾT QUẢ THỰC HIỆN NHÓM 3: TÀI LIỆU CHO BÁO CÁO

Đã hoàn thành biên soạn toàn bộ 6 tài liệu báo cáo kỹ thuật chuyên sâu bằng tiếng Việt chuẩn mực, cấu trúc bảng biểu và sơ đồ Mermaid hoàn chỉnh trong thư mục [docs/bao-cao/](docs/bao-cao/):

1. **[docs/bao-cao/01-tong-quan.md](docs/bao-cao/01-tong-quan.md)**:
   - Mục tiêu, phạm vi bãi đỗ xe 40 ô (hàng E vật lý, hàng A-D mô phỏng).
   - Mô hình đặt chỗ theo khung giờ $T \pm 15$ phút kết hợp khách vãng lai.
   - Bảng chuyển 9 trạng thái vị trí ô (`ViTriDoXe`) và 11 trạng thái đơn đặt chỗ (`DonDatCho`).
   - Bảng 14 tham số cấu hình chính sách hệ thống (`ParkingPolicySettings`).
2. **[docs/bao-cao/02-kien-truc.md](docs/bao-cao/02-kien-truc.md)**:
   - Sơ đồ khối Mermaid 3 tầng phân tán: Phần cứng 6 board ESP32 - HiveMQ Broker - Backend ASP.NET Core 8 - Web PWA SignalR.
   - Hợp đồng danh mục 14 topic MQTT với cờ retain và QoS chuẩn hóa.
   - Sơ đồ ERD Mermaid liên kết 13 bảng cơ sở dữ liệu SQL Server.
   - Phân tích 4 lý do chọn kiến trúc phân tán không dây WiFi/MQTT; các giới hạn của mô hình thực nghiệm và lộ trình thương mại hóa.
3. **[docs/bao-cao/03-thach-thuc-giai-phap.md](docs/bao-cao/03-thach-thuc-giai-phap.md)**:
   - Phân tích chi tiết 14+ tình huống theo cấu trúc: *Tình huống $\rightarrow$ Hậu quả $\rightarrow$ Giải pháp $\rightarrow$ Cách kiểm thử (Unit test & Scenario)*.
   - Bao quát toàn diện: Chống đặt trùng, hủy đơn giữ chỗ 5 phút, đến muộn NO_SHOW, mở barie bỏ đi, vào rồi ra ngay, bãi đông kẹt xe Gate HOLD & Jam extension, hết hạn vào ô FreeExit, đỗ lấn vạch OVERLAP, đỗ chui, mất điện từng board LWT, quá giờ OVERSTAY thu phạt 200đ/phút, thanh toán trùng Idempotency, tranh chấp đồng thời Optimistic Concurrency, mất kết nối Broker với Exponential Backoff.
4. **[docs/bao-cao/04-kich-ban-demo.md](docs/bao-cao/04-kich-ban-demo.md)**:
   - Kịch bản bảo vệ đồ án 10 phút từng bước (mở những cửa sổ nào, thao tác ở trang nào, kết quả trên màn hình và Wokwi, lời thoại thuyết minh với Hội đồng).
   - Kế hoạch dự phòng (Contingency Plan) 3 cấp độ khi mạng WiFi trường hoặc Broker Wokwi bị trục trặc.
5. **[docs/bao-cao/05-huong-dan-chay.md](docs/bao-cao/05-huong-dan-chay.md)**:
   - Hướng dẫn cài đặt từ đầu: SQL Server, user-secrets, dotnet, PlatformIO, Wokwi.
   - Thứ tự khởi động 4 bước bằng Sequence Diagram.
   - Bảng xử lý 6 sự cố phổ biến: Cổng 5137 bận, sqlcmd instance, wokwi.toml path, firmware LCD trắng, tin retained cũ trên HiveMQ, SignalR reconnect.
6. **[docs/bao-cao/06-cau-hoi-thuong-gap.md](docs/bao-cao/06-cau-hoi-thuong-gap.md)**:
   - Tổng hợp 20 câu hỏi phản biện sắc bén của giảng viên kèm câu trả lời ngắn gọn, chuẩn xác dựa trên mã nguồn thật của hệ thống.
7. **Cập nhật mục lục [README.md](README.md) và biên soạn [PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md)**:
   - Cây thư mục dự án toàn diện, phân định rõ ràng các tầng nghiệp vụ và thành phần hệ thống.

---

## 4. KẾT QUẢ THỰC HIỆN NHÓM 4: KIỂM TRA CUỐI CÙNG (FINAL VERIFICATION)

### 4.1 Kết quả kiểm tra toàn diện
1. **Kiểm tra tính bất biến của Database Scripts (Idempotency)**:
   - Chạy `sqlcmd -S .\SQLEXPRESS -E -i QL_BAIGIUXE_IOT.sql` liên tiếp **2 lần**: Kết quả cả 2 lần đều **thành công 100%, 0 lỗi**.
   - Chạy `sqlcmd -S .\SQLEXPRESS -E -i QL_BAIGIUXE_IOT_TEST.sql` liên tiếp **2 lần**: Kết quả cả 2 lần đều **thành công 100%, 0 lỗi**.
2. **Kiểm tra Biên dịch Backend**:
   - `dotnet build backend/SmartParking.Api.csproj`: **Build succeeded (0 warning, 0 error)**.
3. **Kiểm tra Unit Test Suite**:
   - `dotnet test test/SmartParking.Tests/SmartParking.Tests.csproj`: **32/32 tests PASSED (0 failed, thời gian 2 giây)**.
4. **Kiểm tra Biên dịch Firmware PlatformIO**:
   - `pio run` cho cả 6 môi trường vi điều khiển: **6 succeeded in 00:00:40 (gate, row_e, row_a, row_b, row_c, row_d)**.
5. **Kiểm tra liên hoàn 12 kịch bản nghiệp vụ (3 vòng lặp liên tiếp không reset)**:
   - Test `Test32_Chay12KichBanLienTiep3Lan_KhongReset_ThanhCong100PhanTram`: **Thành công 100% (Passed)**.
6. **Cập nhật nhị phân Wokwi**:
   - Đã đồng bộ file `firmware.bin` và `firmware.elf` mới nhất vào cả 2 thư mục [wokwi/gate/](wokwi/gate/) và [wokwi/row_e/](wokwi/row_e/).

### 4.2 Bảng tổng hợp các file Thêm / Sửa / Xóa trong toàn bộ quá trình

| Loại thao tác | Đường dẫn file | Nội dung thay đổi chính |
|:---:|:---|:---|
| **Thêm mới** | `docs/bao-cao/01-tong-quan.md` | Tài liệu báo cáo tổng quan nghiệp vụ, bảng trạng thái, tham số |
| **Thêm mới** | `docs/bao-cao/02-kien-truc.md` | Sơ đồ khối 3 tầng Mermaid, hợp đồng topic, ERD 13 bảng |
| **Thêm mới** | `docs/bao-cao/03-thach-thuc-giai-phap.md` | 14+ tình huống nghiệp vụ, hậu quả, giải pháp và test |
| **Thêm mới** | `docs/bao-cao/04-kich-ban-demo.md` | Kịch bản bảo vệ 10 phút và kế hoạch dự phòng 3 cấp |
| **Thêm mới** | `docs/bao-cao/05-huong-dan-chay.md` | Hướng dẫn cài đặt từ đầu và xử lý 6 sự cố |
| **Thêm mới** | `docs/bao-cao/06-cau-hoi-thuong-gap.md` | 20 câu hỏi phản biện thường gặp của giảng viên |
| **Thêm mới** | `PROJECT_STRUCTURE.md` | Cây thư mục dự án toàn diện, chi tiết từng module |
| **Thêm mới** | `backend/Controllers/DevicesController.cs` | API `/api/devices` trả về danh sách và trạng thái 6 board |
| **Thêm mới** | `tools/clean_retained_mqtt.py` | Công cụ Python dọn dẹp tin retained rác trên HiveMQ |
| **Chỉnh sửa** | `backend/Controllers/ThongKeController.cs` | Thêm API tỷ lệ lấp đầy, số đơn theo trạng thái, xuất CSV UTF-8 |
| **Chỉnh sửa** | `backend/Controllers/LichSuController.cs` | Bổ sung lọc nhật ký hệ thống theo mức độ và thời gian |
| **Chỉnh sửa** | `backend/Controllers/GateController.cs` | Thêm API lấy danh sách mã bị khóa `GET /api/gate/entry-code/locked` |
| **Chỉnh sửa** | `backend/Services/GateService.cs` | Bổ sung `GetLockedCodesAsync()` truy vấn mã LOCKED |
| **Chỉnh sửa** | `backend/Services/MqttBackgroundService.cs` | Exponential Backoff reconnect, phục hồi RAM OVERLAP, replay LED màu không retain |
| **Chỉnh sửa** | `dashboard/admin.html` | Thêm 3 biểu đồ SVG tự vẽ, quản lý 6 board, lọc log, xuất CSV |
| **Chỉnh sửa** | `dashboard/customer.html` | Bổ sung lịch sử đơn tiếng Việt, khung chính sách, toast cảnh báo $< 2$ phút |
| **Chỉnh sửa** | `dashboard/staff.html` | Thêm Bảng việc cần xử lý gấp 4 mức ưu tiên (OVERSTAY, kẹt xe, locked) |
| **Chỉnh sửa** | `README.md` | Thêm mục lục dẫn tới các tài liệu báo cáo mới |
| **Chỉnh sửa** | `QL_BAIGIUXE_IOT.sql` | Bổ sung bảng ThietBi, tối ưu cờ idempotent cho index và seed data |
| **Chỉnh sửa** | `QL_BAIGIUXE_IOT_TEST.sql` | Đồng bộ schema bảng ThietBi cho môi trường kiểm thử độc lập |
| **Xóa bỏ** | `backend/Models/MqttReliableEvent.cs` | Xóa entity di sản thừa |
| **Xóa bỏ** | `backend/Models/MqttEventInbox.cs` | Xóa entity di sản thừa |
| **Xóa bỏ** | `backend/Dtos/ReliableEventDto.cs` | Xóa DTO không dùng |
| **Xóa bỏ** | `backend/Services/ReliableEventService.cs` | Xóa service thừa |
| **Xóa bỏ** | `backend/Services/ParkingSyncService.cs` | Xóa service thừa |
| **Xóa bỏ** | `backend/Services/SnapshotAuditService.cs` | Xóa service thừa |
| **Xóa bỏ** | `backend/Data/SmartParkingDbContext.Sync.cs` | Xóa DbContext partial thừa |
| **Xóa bỏ** | `backend/Controllers/WeatherForecastController.cs`| Xóa controller mẫu của template |
| **Xóa bỏ** | `backend/WeatherForecast.cs` | Xóa model mẫu |

---

## 5. HƯỚNG DẪN THỰC NGHIỆM THỦ CÔNG TRÊN WOKWI (DÀNH CHO NGƯỜI DÙNG)

Để tự mình trải nghiệm tương tác với phần cứng mô phỏng, người dùng thực hiện theo các bước cụ thể sau:

### Bước 1: Khởi động Backend API
Mở một cửa sổ PowerShell riêng biệt và chạy:
```powershell
cd E:\IOT\IOTCODE\IOT_SmartParking\backend
dotnet run
```
*Đợi dòng log*: `Now listening on: http://localhost:5137` và `Connected to MQTT broker broker.hivemq.com:1883`.

### Bước 2: Chạy mô phỏng Wokwi
Có 2 cách chạy mô phỏng:
- **Cách A (Trong Visual Studio Code)**:
  1. Cài extension **Wokwi Simulator**.
  2. Mở file `wokwi/gate/diagram.json` $\rightarrow$ Nhấn tổ hợp `F1` $\rightarrow$ chọn **Wokwi: Start Simulation**.
  3. Mở thêm cửa sổ VS Code thứ hai với file `wokwi/row_e/diagram.json` $\rightarrow$ chọn **Wokwi: Start Simulation**.
- **Cách B (Mô phỏng qua web / demo.html)**:
  - Nếu không muốn mở 2 cửa sổ Wokwi, chỉ cần truy cập `http://localhost:5137/demo.html`. Giao diện bảng điều khiển sẽ thay mặt Wokwi gửi các bản tin MQTT tương đương và hiển thị kết quả ngay lập tức.

### Bước 3: Thao tác tương tác thử nghiệm
1. **Thử nghiệm hiển thị LCD & Barie cổng vào**:
   - Trên trình duyệt, truy cập `http://localhost:5137/customer.html`, đặt ô **E7** và bấm xác nhận chuyển khoản.
   - Nhìn sang cửa sổ Wokwi `gate`: Servo cổng vào sẽ quay góc $90^\circ$, màn hình LCD 1602 hiển thị `CHAO MUNG O DO: E7`.
2. **Thử nghiệm cảm biến khoảng cách & Đèn trạng thái**:
   - Trên cửa sổ Wokwi `row_e`: Click vào cảm biến `sensor_e7`, kéo thanh trượt khoảng cách từ $200\text{ cm}$ xuống $60\text{ cm}$.
   - Quan sát đèn LED NeoPixel thứ 7: Đèn lập tức chuyển sang màu **ĐỎ** (`OCCUPIED`).
   - Mở trang `http://localhost:5137/admin.html`: Ô E7 trên sơ đồ 40 ô đồng thời chuyển sang màu đỏ thời gian thực.
3. **Thử nghiệm đỗ lấn vạch (OVERLAP)**:
   - Trên Wokwi `row_e`: Kéo cả `sensor_e1` và `sensor_e2` về khoảng $65\text{ cm}$.
   - Quan sát: Cả 2 đèn LED E1 và E2 cùng chớp màu **VÀNG**. Bàn trực nhân viên `http://localhost:5137/staff.html` nhận ngay cảnh báo đỗ lấn vạch.
