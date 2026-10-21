function attachReadmissionAction(container, scanRequest, accessToken) {
  const request = { ...scanRequest, allowReadmission: true, requestId: crypto.randomUUID() };
  const panel = document.createElement('section');
  panel.className = 'readmission-panel';
  panel.innerHTML = `<div class="readmission-header">
      <span class="readmission-icon material-symbols-outlined" aria-hidden="true">verified_user</span>
      <div><h3>Xác nhận chủ vé</h3><p>Cho vào bổ sung sau khi đã đối chiếu chủ vé thật.</p></div>
      <button type="button" data-open class="readmission-button"><span class="material-symbols-outlined" aria-hidden="true">edit_note</span>Cho vào có ghi chú</button>
    </div>
    <form hidden class="readmission-form">
      <label>Lý do cho vào bổ sung <span class="readmission-required">*</span>
        <textarea name="reason" required maxlength="1000" rows="3" placeholder="Ví dụ: Đã đối chiếu chủ vé, khách ra ngoài và quay lại"></textarea>
      </label>
      <div class="readmission-footer"><p>Lưu lý do, thời điểm và nhân viên xác nhận.<br>Lần vào đầu tiên được giữ nguyên.</p>
        <div class="readmission-actions"><button type="button" data-cancel class="readmission-cancel">Hủy</button>
        <button type="submit" class="readmission-button"><span class="material-symbols-outlined" aria-hidden="true">check_circle</span>Xác nhận cho vào</button></div>
      </div>
    </form><p role="status" class="readmission-status"></p>`;
  const card = container.querySelector('.result-card-outdoor');
  card.insertBefore(panel, card.lastElementChild);
  const open = panel.querySelector('[data-open]');
  const form = panel.querySelector('form');
  const submit = panel.querySelector('[type=submit]');
  const status = panel.querySelector('[role=status]');
  open.onclick = () => { form.hidden = false; open.hidden = true; form.elements.reason.focus(); };
  let saving = false;
  panel.querySelector('[data-cancel]').onclick = () => { if (saving) return; form.hidden = true; open.hidden = false; status.textContent = ''; };
  form.onsubmit = async event => {
    event.preventDefault();
    if (saving) return;
    const reason = form.elements.reason.value.trim();
    if (!reason || reason.length > 1000) { status.textContent = 'Vui lòng nhập lý do từ 1 đến 1000 ký tự.'; return; }
    saving = true; panel.querySelector('[data-cancel]').disabled = true; submit.disabled = true; form.elements.reason.disabled = true;
    status.textContent = 'Đang ghi nhận lần vào bổ sung…';
    try {
      const response = await fetch('/api/v1/checkin/scan', {
        method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + accessToken },
        body: JSON.stringify({ ...request, readmissionReason: reason })
      });
      const data = await response.json();
      if (!panel.isConnected) return;
      if (!response.ok) {
        if (data.reason === 'READMISSION_ALREADY_RECORDED') {
          form.hidden = true; status.textContent = 'Lần vào bổ sung này đã được ghi nhận. Không tạo thêm lần vào.'; return;
        }
        throw new Error(data.message || 'Không thể cho vào bổ sung.');
      }
      form.hidden = true;
      status.style.color = '#166534'; status.style.fontWeight = '700';
      status.textContent = `Đã cho vào bổ sung có ghi chú.\nNhân viên: ${data.staffName}\nCửa: ${data.gate}\nThời điểm: ${new Date(data.checkInTime).toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' })}\nLý do: ${data.readmissionReason}`;
    } catch (error) { if (panel.isConnected) status.textContent = error.message; }
    finally { saving = false; panel.querySelector('[data-cancel]').disabled = false; submit.disabled = false; form.elements.reason.disabled = false; }
  };
}
