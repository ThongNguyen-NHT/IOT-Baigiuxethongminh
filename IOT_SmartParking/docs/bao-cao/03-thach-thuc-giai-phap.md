# BÁO CÁO CÁC THÁCH THỨC NGHIỆP VỤ VÀ GIẢI PHÁP KỸ THUẬT

> **Dự án**: Smart Parking IoT Engineering Challenges & Solutions  
> **Cấu trúc mỗi tình huống**: Tình huống $\rightarrow$ Hậu quả nếu không xử lý $\rightarrow$ Giải pháp kỹ thuật $\rightarrow$ Cách kiểm thử & Kịch bản thực nghiệm $\rightarrow$ Trạng thái triển khai

---

## 1. ĐẶT TRÙNG Ô VÀ ĐẶT CHỒNG KHUNG GIỜ (DOUBLE BOOKING)

- **Tình huống**: Hai khách hàng cùng mở ứng dụng và cùng bấm đặt một vị trí ô đỗ (ví dụ ô E1) vào cùng một thời điểm, hoặc đặt các khung giờ cách nhau quá ngắn.
- **Hậu quả nếu không xử lý**: Xảy ra tranh chấp tại cổng bãi xe, hai xe cùng được cấp quyền vào một ô đỗ duy nhất, gây ùn ứ và mất uy tín hệ thống.
- **Giải pháp kỹ thuật**:
  - Tầng Cơ sở dữ liệu: Thiết lập kiểm tra khóa hàng (Row Lock / Serializable Transaction) và kiểm tra khoảng đệm an toàn bắt buộc `MinGapBetweenBookingsMin = 30 phút` giữa hai khung giờ đỗ:
    $$[T_{start1}, T_{end1} + 30'] \cap [T_{start2}, T_{end2}] = \emptyset$$
  - Đơn đầu tiên gửi lên thành công sẽ chuyển ô sang `HELD`, các yêu cầu song song kế tiếp bị từ chối ngay lập tức với lỗi `Conflict (409)`.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test1_HaiKhachGiuCungMotOCungLuc_TaskWhenAll_ChiMotNguoiThanhCong` (chạy song song qua `Task.WhenAll`), `Test4_DatChongKhungGioVaKhoangDemMinGap_BiTuChoi`, `Test16_TichHopSqlServerThat_HaiDbContextSongSong_Lap20Lan_KhongDatTrung`.
  - Kịch bản Demo: Kịch bản 1 (`scenario 1`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100% trong Backend**.

---

## 2. GIỮ CHỖ KHÔNG THANH TOÁN (HOLD TIMEOUT)

- **Tình huống**: Khách hàng tạo đơn giữ chỗ (`HELD`) nhưng vì lý do nào đó không thực hiện chuyển khoản hoặc hủy thanh toán.
- **Hậu quả nếu không xử lý**: Ô đỗ bị chiếm dụng ảo vô thời hạn, khách hàng thật có nhu cầu không thể đặt được chỗ, làm thất thu bãi xe.
- **Giải pháp kỹ thuật**:
  - Hệ thống gắn thời hạn `HanThanhToan = ThoiGianDat + 5 phút`.
  - Tiến trình nền `ParkingTimeoutWorker` định kỳ 10 giây quét cơ sở dữ liệu. Mọi đơn `PENDING_PAYMENT` vượt quá 5 phút sẽ bị chuyển sang `EXPIRED`, đồng thời hoàn trả trạng thái vị trí ô về `FREE`.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test6_DonKhongThanhToanSau5Phut_ExpiredVaNhaO`.
  - Kịch bản Demo: Kịch bản 2 (`scenario 2`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 3. KHÁCH ĐÃ THANH TOÁN NHƯNG ĐẾN MUỘN (NO-SHOW)

- **Tình huống**: Khách đã thanh toán tiền giữ chỗ nhưng quá giờ hẹn vẫn không đến quét mã vào cổng.
- **Hậu quả nếu không xử lý**: Ô đỗ tiếp tục bị phong tỏa không rõ xe có đến hay không, gây lãng phí tài nguyên ô đỗ.
- **Giải pháp kỹ thuật**:
  - Quy định khung giờ check-in: $[T - 15', T + 15']$.
  - Sau mốc $T + 15$ phút, nếu đơn vẫn ở trạng thái `PAID` (chưa chuyển `ENTERED`), `ParkingTimeoutWorker` tự động cập nhật đơn sang `NO_SHOW`.
  - **Chính sách tài chính**: Khoản tiền đã trả **không được hoàn lại** (Non-refundable) để bù đắp chi phí cơ hội giữ chỗ. Ô đỗ được trả về `FREE` (nếu không có đơn kế tiếp giữ ô).
- **Cách kiểm thử tự động**:
  - Unit Test: `Test7_QuaHanCheckIn15Phut_NoShowKhongHoanTien`, `Test14_DonANoShowNhungDonBGiuCungOKhungGioSau_OKhongBiNhaFreeSai`.
  - Kịch bản Demo: Kịch bản 3 (`scenario 3`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 4. QUÉT MÃ MỞ BARIE RỒI BỎ ĐI (GATE PASS ABANDONED)

- **Tình huống**: Khách quét mã hợp lệ tại cổng vào, barie mở lên nhưng xe lùi lại bỏ đi hoặc đổi ý không lái qua cổng.
- **Hậu quả nếu không xử lý**: Cổng bị kẹt ở trạng thái mở, hoặc đơn hàng bị treo ở trạng thái đã vào bãi (`ENTERED`) làm sai lệch số lượng xe thực tế.
- **Giải pháp kỹ thuật**:
  - Cổng có cảm biến chùm tia hồng ngoại (`sensor_gate_in`). Cổng chỉ kích hoạt chuyển đơn sang `ENTERED` khi nhận bản tin MQTT `PASS` (tức xe đã cắt qua chùm tia).
  - Nếu sau `transitSec = 10 giây` không có xe đi qua, servo barie tự động hạ xuống đóng cổng. Mã EntryCode được phục hồi hoặc hủy tùy theo cấu hình thời gian.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test17_CheckInHaiBuoc_QuetDung_MoCong_QuetSaiVuotQuaLimit_BiKhoa`, `Test28_BaoMatCong_TuChoiPassKhiChuaCoLenhMoCong`.
  - Kịch bản Demo: Kịch bản 4 (`scenario 4`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 5. VÀO BÃI RỒI QUAY ĐẦU RA NGAY (ABANDONED IN LOT)

- **Tình huống**: Xe đã quét mã và qua barie cổng vào (`ENTERED`), nhưng khách phát hiện có việc gấp và lập tức quay đầu lái ra cổng ra mà không hề đỗ vào ô.
- **Hậu quả nếu không xử lý**: Hệ thống tiếp tục giữ ô ở trạng thái `GUIDING`, trong khi xe đã rời khỏi bãi.
- **Giải pháp kỹ thuật**:
  - Khi xe quét mã hoặc bấm nút tại cổng ra khi đơn đang ở trạng thái `ENTERED` (chưa từng chuyển sang `PARKED`), hệ thống nhận diện đây là ca `ABANDONED`.
  - Cho phép mở barie ra, đơn chuyển sang `ABANDONED` và ô đỗ lập tức giải phóng về `FREE`.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test18_XeRaKhiChuaDo_ChuyenSangAbandoned_NhaODo`.
  - Kịch bản Demo: Kịch bản 5 (`scenario 5`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 6. BÃI ĐÔNG XE VÀ ĐI CHẬM TÌM Ô (HOLD & JAM EXTENSION)

- **Tình huống**: Đường nội bộ trong bãi xe bị ùn ứ tạm thời, nhiều xe cùng di chuyển tìm chỗ, hoặc xe gặp chướng ngại vật đi quá thời gian 10 phút.
- **Hậu quả nếu không xử lý**: Cổng tiếp tục nhận thêm xe làm bãi kẹt cứng; xe đi chậm bị hệ thống phạt hủy đơn oan uổng.
- **Giải pháp kỹ thuật**:
  - Cơ chế **Gate HOLD**: Khi số lượng xe đang tìm ô đạt ngưỡng `MaxInLotSearching = 5 xe`, cổng vào tạm thời không mở thêm xe mới (phát thông điệp "Bãi đang đông, vui lòng chờ"). Khi số xe tìm ô giảm xuống dưới ngưỡng, cổng tự động `UNHOLD`.
  - Cơ chế **Gia hạn kẹt xe**: Khách hàng có thể bấm nút "Tôi đang kẹt xe" trên Web PWA hoặc bấm nút trạm phụ `CP1`/`CP2` để được cộng thêm 5 phút di chuyển (tối đa 1 lần).
- **Cách kiểm thử tự động**:
  - Unit Test: `Test19_BaiDongXeDangTimCho_MaxInLotSearching_TuChoiMoCong`, `Test20_NutToiDangKet_VaCheckpoint_GiaHanThoiGianVaoO`, `Test24_Checkpoint_LapLaiCungCheckpoint_KhongGiaHanThem`, `Test25_BaiDong_Hold_KhiXeTimGiamXuong_CongTuMoLai_VaXeChoKhongBiTinhHanDo`.
  - Kịch bản Demo: Kịch bản 6 (`scenario 6`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 7. HẾT HẠN VÀO Ô ĐỖ (SLOT REASSIGNMENT & FREE EXIT)

- **Tình huống**: Xe qua cổng vào quá 10 phút (kể cả thời gian gia hạn) nhưng cảm biến tại ô đỗ vẫn không thấy xe vào (ví dụ khách phát hiện ô bị vật cản hoặc bãi xe hết sạch chỗ).
- **Hậu quả nếu không xử lý**: Đơn bị treo lơ lửng, ô đỗ không thể tiếp nhận xe khác.
- **Giải pháp kỹ thuật**:
  - `ParkingTimeoutWorker` quét các đơn `ENTERED` quá hạn `HanDoVaoO`:
    - Nếu bãi còn ô trống khác: Tự động điều phối đổi sang ô mới (`REASSIGN`) và kích hoạt dải LED dẫn đường tới ô mới.
    - Nếu toàn bãi không còn ô nào trống: Kích hoạt chính sách `NoSlotPolicy = FreeExit`, cấp quyền cho xe ra cổng miễn phí không thu tiền.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test21_HetHanVaoOMaChuaDo_TuDoiOSangFree_NeuHetChoThiFreeExit`.
  - Kịch bản Demo: Kịch bản 7 (`scenario 7`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 8. ĐỖ XE LẤN VẠCH (OVERLAP DETECTION)

- **Tình huống**: Tài xế đỗ xe xiên hoặc chèn lên vạch sơn ngăn cách giữa 2 ô liền kề (ví dụ giữa E1 và E2).
- **Hậu quả nếu không xử lý**: Làm hẹp không gian, xe khác không thể đỗ vào ô bên cạnh dù hệ thống vẫn báo ô đó trống.
- **Giải pháp kỹ thuật**:
  - Cảm biến siêu âm trên 2 ô kề nhau sẽ cùng đo được khoảng cách lơ lửng trong dải $40 \text{ cm} \le d \le 80 \text{ cm}$ (không đủ gần $< 100$ cm để tính đỗ hẳn, nhưng không đủ xa $> 120$ cm để tính trống).
  - Thuật toán nội suy tại Backend ghi nhận trạng thái cặp ô chuyển sang `OVERLAP`, phát lệnh MQTT đổi LED của cả 2 ô sang **màu VÀNG nhấp nháy** và đẩy cảnh báo tức thời lên `admin.html`/`staff.html`.
  - Trạng thái đo đạc được lưu đệm trong RAM và tự động tái tính toán sau khi Backend khởi động lại.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test22_OverlapBangKhoangCach_PartitalVaAdjacent_PhatHienLanVach`, `Test30_Overlap_KhoiPhucTuDatabase_SauKhiKhoiDongLai`.
  - Kịch bản Demo: Kịch bản 8 (`scenario 8`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 9. ĐỖ NHẦM Ô VÀ ĐỖ XE CHUI (UNAUTHORIZED PARKING)

- **Tình huống**: Xe A đặt ô E1 nhưng lại đỗ vào ô E2; hoặc xe vào đỗ khi không có bất kỳ đơn đặt chỗ nào trong hệ thống.
- **Hậu quả nếu không xử lý**: Xe B đặt đúng ô E2 khi đến nơi phát hiện ô đã bị chiếm, gây xung đột trực tiếp.
- **Giải pháp kỹ thuật**:
  - Cơ chế **Resync Audit**: Định kỳ hoặc khi phát hiện cảm biến nhận xe mà ô đó không có đơn `ENTERED` nào tương ứng, Backend lập tức phát cảnh báo `UNAUTHORIZED_PARKING`.
  - Bàn trực `staff.html` nhận thông báo vị trí ô bị chiếm chui để nhân viên điều phối di dời hoặc khóa bánh.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test23_MatDienDutMangLWT_DanhDauUnknown_VaAuditXeDoChui`.
  - Kịch bản Demo: Kịch bản 9 (`scenario 9`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 10. MẤT ĐIỆN VÀ MẤT MẠNG TỪNG BOARD (LWT PARTIAL FAILURE)

- **Tình huống**: Board ESP32 quản lý hàng E (`esp32_row_e`) bị mất nguồn hoặc đứt cáp nguồn, trong khi các board khác vẫn hoạt động bình thường.
- **Hậu quả nếu không xử lý**: Backend giữ trạng thái cũ sai lệch, tiếp tục điều xe vào hàng ô đang mất kiểm soát.
- **Giải pháp kỹ thuật**:
  - Mỗi board ESP32 khi kết nối MQTT đều đăng ký bản tin **LWT (Last Will and Testament)** với Broker tại topic `{prefix}/device/{deviceId}/status`, payload `"OFFLINE"`, retain `true`.
  - Ngay khi board mất kết nối, Broker phát tán bản tin LWT. Backend nhận bản tin và **chỉ đánh dấu các ô thuộc hàng đó sang UNKNOWN**, các hàng khác giữ nguyên 100%.
  - Khi có điện trở lại (`ONLINE`), board gửi bản tin snapshot, Backend đối soát và phục hồi trạng thái chuẩn xác.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test23_MatDienDutMangLWT_DanhDauUnknown_VaAuditXeDoChui`, `Test31_MatDienTungBoard_ChiAnhHuongODoThuocHangDo`.
  - Kịch bản Demo: Kịch bản 9 (`scenario 9`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 11. ĐỖ QUÁ GIỜ ĐÃ MUA (OVERSTAY PENALTY)

- **Tình huống**: Khách mua gói 1 giờ ($20.000$ đ) nhưng để xe lại 1 giờ 15 phút.
- **Hậu quả nếu không xử lý**: Trốn phí đỗ xe, chiếm chỗ của khách đặt khung giờ kế tiếp.
- **Giải pháp kỹ thuật**:
  - Đến giờ kết thúc gói, đơn tự động chuyển sang `OVERSTAY`.
  - Khi xe ra đến barie cổng ra, hệ thống từ chối mở tự động và hiển thị cảnh báo:
    $$\text{Tiền phạt} = \text{Số phút quá giờ} \times 200 \text{ VNĐ/phút}$$
  - Nhân viên thu ngân đối soát, thu đúng số tiền phạt và xác nhận qua API `/api/gate/out/overstay/confirm` thì barie cổng ra mới mở cho xe đi qua.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test27_XeQuaGioDat_Overstay_ChanCongRa_ThuPhatMoiMo`.
  - Kịch bản Demo: Kịch bản 10 (`scenario 10`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 12. THANH TOÁN TRÙNG VÀ IDEMPOTENCY

- **Tình huống**: Cổng thanh toán ngân hàng phát lại webhook 2 lần do mạng chập chờn, hoặc khách hàng bấm xác nhận thanh toán nhiều lần liên tiếp.
- **Hậu quả nếu không xử lý**: Đơn bị ghi nhận doanh thu 2 lần, hoặc xung đột trạng thái dữ liệu.
- **Giải pháp kỹ thuật**:
  - Thiết kế Idempotency Key duy nhất dựa trên `MaGiaoDichDuyNhat` và kiểm tra khóa trạng thái: Nếu đơn đã ở trạng thái `PAID` thì các bản tin xác nhận tiếp theo sẽ bỏ qua an toàn và trả về mã thành công ngay lập tức.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test8_WebhookNganHangThat_KiemTraChuKyHmacSoTienVaIdempotent`.
  - Kịch bản Demo: Kịch bản 11 (`scenario 11`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 13. TRANH CHẤP ĐỒNG THỜI (RACE CONDITION: WORKER VS THANH TOÁN)

- **Tình huống**: Đúng giây thứ 300 (phút thứ 5), tiến trình nền `ParkingTimeoutWorker` chuẩn bị hủy đơn `EXPIRED` thì webhook ngân hàng báo thanh toán thành công đổ về cùng một phần nghìn giây.
- **Hậu quả nếu không xử lý**: Khách đã bị trừ tiền trong tài khoản nhưng hệ thống lại hủy đơn và giải phóng ô cho người khác.
- **Giải pháp kỹ thuật**:
  - Áp dụng kỹ thuật **Optimistic Concurrency Control** thông qua cột phiên bản dòng `RowVersion` (Timestamp byte[]) trong EF Core và khóa giao dịch `TransactionScope`.
  - Tiến trình thanh toán luôn có độ ưu tiên cao hơn: Nếu đơn đang trong tích tắc chuyển giao, hệ thống ưu tiên ghi nhận trạng thái `PAID` và cấp vé cho khách hàng.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test13_ThanhToanVaWorkerChayDongThoi_ChiDuyNhatMotTrangThaiCuoiCung_KhongMatTien`.
  - Kịch bản Demo: Kịch bản 12 (`scenario 12`) trên `demo.html`.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 14. MẤT KẾT NỐI BROKER MQTT VÀ TỰ ĐỘNG PHỤC HỒI

- **Tình huống**: Broker MQTT công cộng khởi động lại hoặc mạng internet bị rớt gói.
- **Hậu quả nếu không xử lý**: Backend mất liên lạc vĩnh viễn với thiết bị phần cứng, các sự kiện bấm nút hoặc cảm biến bị bỏ lỡ.
- **Giải pháp kỹ thuật**:
  - Triển khai thuật toán **Exponential Backoff Reconnect** trong `MqttBackgroundService`: Khoảng cách các lần thử kết nối lại tăng dần theo cấp số ($1s \rightarrow 2s \rightarrow 5s \rightarrow 10s \rightarrow 30s$).
  - Ngay khi kết nối thành công, tự động gọi lại hàm `SubscribeTopicsAsync()` subscribe đủ 6 topic bắt buộc và phát lệnh resync Audit để đồng bộ lại trạng thái.
- **Cách kiểm thử tự động**:
  - Unit Test: `Test29_MqttReconnect_VaResubscribeAllTopics`.
  - Trạng thái kiểm thử: Đã chạy thực tế với HiveMQ.
- **Trạng thái**: **Đã triển khai hoàn tất 100%**.

---

## 15. TỔNG HỢP SO SÁNH: ĐÃ TRIỂN KHAI VS HƯỚNG PHÁT TRIỂN

| Hạng mục nghiệp vụ | Đã triển khai hoàn chỉnh trong đồ án | Hướng phát triển công nghiệp (Production) |
|:---|:---|:---|
| **Chống đặt trùng** | Concurrency Lock + 30 phút MinGap an toàn | Phân bổ ô động bằng giải thuật AI (Dynamic Slot Allocation) |
| **Thanh toán** | VietQR mã hóa nội dung + Webhook Idempotency + CashCode 6 ký tự | Kết nối trực tiếp cổng thanh toán NAPAS / VNPay có ký số HSM |
| **Nhận diện xe** | So khớp biển số trên giao diện Web & Cảm biến chùm tia | Camera AI ANPR nhận diện biển số thời gian thực dưới 0.3s |
| **Phát hiện lấn vạch** | Thuật toán đo cự ly $40-80$ cm trên 2 ô kề nhau | Kết hợp cảm biến từ trường gắn sàn (Geomagnetic sensor) |
| **Phục hồi sự cố** | LWT + Snapshot + Exponential Backoff Reconnect | Cụm MQTT Broker High Availability (EMQX Cluster) |
