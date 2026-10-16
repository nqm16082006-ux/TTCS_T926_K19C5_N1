function openDuplicateShowtime(showtimeId) {
  const source = currentShowtimes.find(s => s.id === showtimeId);
  if (!source) return;
  const dialog = document.createElement('dialog');
  dialog.className = 'showtime-copy-dialog';
  dialog.setAttribute('aria-label', 'Nhân bản suất diễn');
  dialog.innerHTML = `<form>
    <div class="showtime-copy-header">
      <div class="showtime-copy-icon"><span class="material-symbols-outlined" aria-hidden="true">content_copy</span></div>
      <div><h2>Nhân bản suất diễn</h2><p>Tái sử dụng sơ đồ và bảng giá cho một lịch diễn mới.</p></div>
    </div>
    <div class="showtime-copy-body">
      <div class="showtime-copy-source"><span>Lịch của suất gốc</span><strong>${new Date(source.startTime).toLocaleString('vi-VN')} → ${new Date(source.endTime).toLocaleString('vi-VN')}</strong></div>
      <label class="showtime-copy-label">Tên suất diễn mới<input name="showtimeName" type="text" maxlength="200" placeholder="Ví dụ: Đêm Chung kết"></label>
      <p class="showtime-copy-label">Chọn lịch cho suất mới</p>
      <div class="showtime-copy-grid">
        <label>Bắt đầu<input name="start" type="datetime-local" required></label>
        <label>Kết thúc<input name="end" type="datetime-local" required></label>
      </div>
      <label class="showtime-copy-label">Hạn bán vé<input name="salesEndTime" type="datetime-local"></label>
      <p class="showtime-copy-hint">Để trống hạn bán: dùng giờ bắt đầu suất mới.</p>
      <p class="showtime-copy-hint">Đang gợi ý ngày kế tiếp. Bạn có thể điều chỉnh trước khi tạo.</p>
      <div class="showtime-copy-summary"><span>Suất mới: Nháp</span><span>Giữ sơ đồ &amp; bảng giá</span><span>Tất cả ghế trống</span></div>
      <p role="alert" class="showtime-copy-status"></p>
    </div>
    <div class="showtime-copy-footer"><button type="button" data-close class="showtime-copy-cancel">Hủy</button><button type="submit" class="showtime-copy-confirm"><span class="material-symbols-outlined" aria-hidden="true">add</span>Tạo suất diễn nháp</button></div>
  </form>`;
  document.body.appendChild(dialog);
  const form = dialog.querySelector('form');
  form.elements.showtimeName.value = source.name ? source.name + ' (bản sao)' : '';
  const alertBox = dialog.querySelector('[role=alert]');
  const buttons = dialog.querySelectorAll('button');
  form.elements.start.value = formatDateTimeLocal(new Date(new Date(source.startTime).getTime() + 86400000).toISOString());
  form.elements.end.value = formatDateTimeLocal(new Date(new Date(source.endTime).getTime() + 86400000).toISOString());
  dialog.querySelector('[data-close]').onclick = () => dialog.close();
  dialog.addEventListener('close', () => dialog.remove());
  let saving = false;
  dialog.addEventListener('cancel', event => { if (saving) event.preventDefault(); });
  form.onsubmit = async event => {
    event.preventDefault();
    if (saving) return;
    const startTime = new Date(form.elements.start.value);
    const endTime = new Date(form.elements.end.value);
    if (!Number.isFinite(startTime.getTime()) || !Number.isFinite(endTime.getTime()) || endTime <= startTime) {
      alertBox.textContent = 'Thời gian kết thúc phải sau thời gian bắt đầu.';
      return;
    }
    saving = true;
    buttons.forEach(button => button.disabled = true);
    alertBox.textContent = 'Đang nhân bản...';
    try {
      const response = await fetch(`${API_BASE}/api/events/${currentEventId}/showtimes/${showtimeId}/duplicate`, {
        method: 'POST', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: form.elements.showtimeName.value.trim(), salesEndTime: form.elements.salesEndTime.value ? new Date(form.elements.salesEndTime.value).toISOString() : startTime.toISOString(), startTime: startTime.toISOString(), endTime: endTime.toISOString() })
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || !result.success) throw new Error(result.message || 'Không thể nhân bản suất diễn.');
      // Close after server success to avoid resubmitting if refreshing the list fails.
      dialog.close();
      await loadShowtimes(currentEventId);
    } catch (error) { alertBox.textContent = error.message; }
    finally { saving = false; buttons.forEach(button => button.disabled = false); }
  };
  dialog.showModal();
}
