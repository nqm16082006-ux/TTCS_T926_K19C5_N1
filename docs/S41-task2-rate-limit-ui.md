# S-41 task 2: phản hồi giới hạn trên giao diện

Trang `seat-map.html` xử lý HTTP 429 ở thao tác giữ ghế và tạo đơn. Thông báo nằm cạnh giỏ vé, hiển thị số giây còn lại; nút thao tác bị vô hiệu hoá trong thời gian chờ. Phản hồi 503 có code RATE_LIMIT_UNAVAILABLE được xử lý tương tự với thông báo hệ thống bận.

Thời gian chờ lấy từ retryAfterSeconds trong JSON, sau đó Retry-After (giây hoặc HTTP date). Khi phản hồi 429 không có thời gian hợp lệ, dùng 60 giây; khi bộ đếm BE không khả dụng, dùng 5 giây.

Mỗi thao tác có thời gian chờ riêng. Thay đổi giỏ vé không xoá thời gian chờ. Hết chờ chỉ mở lại nút nếu còn ghế; người dùng tự bấm thử lại, FE không gửi lại tự động. Ghế nháp, ghế đang giữ, dữ liệu lưu tạm và đơn hiện có không bị xoá do phản hồi giới hạn. Đồng hồ giữ vé hoạt động độc lập và vẫn hết hạn theo quy tắc hiện có.

Thời gian chờ FE nằm trong bộ nhớ trang; tải lại trang sẽ xoá trạng thái FE nhưng BE vẫn áp dụng giới hạn.

Kiểm thử: `node --test src/tests/booking-rate-limit.test.cjs src/tests/frontend-audit.test.cjs` và `node src/tests/hold-countdown-check.cjs`.

Kiểm thử dùng phản hồi API giả lập để kiểm tra luồng giữ ghế/tạo đơn, thao tác lặp trong lúc chờ, mở lại nút, giữ nguyên lựa chọn, thay đổi giỏ, thời gian phản hồi thiếu/sai và lỗi Redis. Chưa kiểm chứng giao diện trong trình duyệt với BE/Redis thật.
