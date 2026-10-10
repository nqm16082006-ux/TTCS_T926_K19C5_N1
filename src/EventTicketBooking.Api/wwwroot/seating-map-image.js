function openSeatingMapImageEditor(button) {
  const showtimeId = button.dataset.showtime;
  const dialog = document.createElement('dialog');
  dialog.style.cssText = 'width:min(700px,95vw);max-height:90vh;border:0;border-radius:16px;padding:24px;overflow:auto';
  dialog.innerHTML = `<form><h2 style="font-size:22px;font-weight:700">Ảnh sơ đồ khán phòng</h2>
    <p style="margin:12px 0">Ảnh minh họa riêng cho suất diễn này. Danh sách ghế và giá vé được quản lý riêng.</p>
    <label style="display:block">Đường link ảnh<input name="url" type="text" placeholder="https://..." style="display:block;width:100%;padding:10px;border:1px solid #ccc;border-radius:8px"></label>
    <label style="display:block;margin-top:16px">Hoặc tải ảnh lên (tối đa 10 MB)<input name="file" type="file" accept=".jpg,.jpeg,.png,.webp,.gif" style="display:block;margin-top:8px"></label>
    <img alt="Xem trước ảnh sơ đồ" style="max-width:100%;max-height:400px;object-fit:contain;margin:16px auto" hidden>
    <p role="status" style="margin:12px 0"></p>
    <div style="display:flex;gap:16px"><button type="submit">Lưu ảnh</button><button type="button" data-remove>Xóa ảnh</button><button type="button" data-close>Đóng</button></div></form>`;
  document.body.appendChild(dialog);
  const form = dialog.querySelector('form'), input = form.elements.url, file = form.elements.file;
  const preview = dialog.querySelector('img'), status = dialog.querySelector('[role=status]');
  let objectUrl;
  input.value = button.dataset.image || '';
  const validUrl = value => value.startsWith('/uploads/events/') || /^https?:\/\//i.test(value);
  function updatePreview(url) {
    preview.hidden = !url;
    if (url) preview.src = url; else preview.removeAttribute('src');
  }
  updatePreview(validUrl(input.value) ? input.value : '');
  preview.onerror = () => { status.textContent = 'Không thể hiển thị ảnh. Kiểm tra đường link hoặc chọn ảnh khác.'; };
  input.addEventListener('input', () => { file.value = ''; status.textContent = ''; updatePreview(validUrl(input.value.trim()) ? input.value.trim() : ''); });
  file.addEventListener('change', () => {
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    if (file.files[0]) { objectUrl = URL.createObjectURL(file.files[0]); updatePreview(objectUrl); status.textContent = ''; }
  });
  dialog.querySelector('[data-close]').onclick = () => dialog.close();
  dialog.addEventListener('close', () => { if (objectUrl) URL.revokeObjectURL(objectUrl); dialog.remove(); });
  async function save(remove) {
    const buttons = dialog.querySelectorAll('button');
    buttons.forEach(b => b.disabled = true);
    status.textContent = 'Đang lưu...';
    try {
      let imageUrl = remove ? null : input.value.trim();
      if (!remove && file.files[0]) {
        if (file.files[0].size > 10 * 1024 * 1024) throw new Error('Ảnh vượt quá 10 MB.');
        const body = new FormData(); body.append('file', file.files[0]);
        const upload = await fetch(`${API_BASE}/api/upload/image`, { method: 'POST', headers: { Authorization: 'Bearer ' + token }, body });
        const result = await upload.json();
        if (!upload.ok || !result.success) throw new Error(result.message || 'Không tải được ảnh.');
        imageUrl = result.data.url;
      }
      if (!remove && !imageUrl) throw new Error('Chọn ảnh hoặc nhập đường link; dùng Xóa ảnh để bỏ ảnh hiện tại.');
      const response = await fetch(`${API_BASE}/api/events/${currentEventId}/showtimes/${showtimeId}/seating-map-image`, {
        method: 'PUT', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' }, body: JSON.stringify({ imageUrl })
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || !result.success) throw new Error(result.message || 'Không lưu được ảnh. Kiểm tra quyền đăng nhập.');
      await loadShowtimes(currentEventId);
      dialog.close();
    } catch (error) { status.textContent = error.message; }
    finally { buttons.forEach(b => b.disabled = false); }
  }
  form.onsubmit = event => { event.preventDefault(); void save(false); };
  dialog.querySelector('[data-remove]').onclick = () => { void save(true); };
  dialog.showModal();
}

function renderSeatingMapOverview(showtimes, activeId) {
  const container = document.getElementById('seatingMapOverview');
  container.replaceChildren();
  const select = document.createElement('select');
  select.style.cssText = 'padding:10px;max-width:100%;margin-bottom:16px';
  select.setAttribute('aria-label', 'Chọn suất diễn để xem sơ đồ khán phòng');
  for (const showtime of showtimes) {
    const option = document.createElement('option');
    option.value = showtime.id;
    option.textContent = new Date(showtime.startTime).toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' });
    select.appendChild(option);
  }
  select.value = showtimes.find(s => s.id === activeId)?.id || showtimes.find(s => s.status === 'OnSale')?.id || showtimes[0]?.id || '';
  const content = document.createElement('div');
  container.append(select, content);
  select.hidden = showtimes.length <= 1;
  function render() {
    content.replaceChildren();
    const showtime = showtimes.find(s => s.id === select.value);
    const url = showtime?.seatingMapImageUrl;
    const message = document.createElement('p');
    message.textContent = 'Ban tổ chức chưa cập nhật ảnh sơ đồ cho suất diễn này.';
    content.appendChild(message);
    if (url && (url.startsWith('/uploads/events/') || /^https?:\/\//i.test(url))) {
      message.hidden = true;
      const link = document.createElement('a');
      link.href = url; link.target = '_blank'; link.rel = 'noopener noreferrer';
      link.setAttribute('aria-label', 'Mở ảnh sơ đồ kích thước đầy đủ');
      const image = document.createElement('img');
      image.src = url; image.alt = 'Sơ đồ khán phòng do ban tổ chức cung cấp';
      image.style.cssText = 'max-width:100%;max-height:800px;margin:auto;object-fit:contain';
      image.onerror = () => { link.hidden = true; message.hidden = false; message.textContent = 'Không tải được ảnh sơ đồ. Vui lòng thử lại sau.'; };
      link.appendChild(image); content.appendChild(link);
      const note = document.createElement('p'); note.textContent = 'Bấm ảnh để xem kích thước đầy đủ.'; content.appendChild(note);
    }
    if (showtime) {
      document.getElementById('totalSeatsBadge').textContent = `Quy mô: ${Number(showtime.totalSeats || 0).toLocaleString('vi-VN')} vị trí`;
      document.getElementById('eventMaxTicketsBadge').textContent = `Tối đa ${showtime.maxTicketsPerUser || 10} vé/suất`;
    }
  }
  select.onchange = render;
  render();
}
