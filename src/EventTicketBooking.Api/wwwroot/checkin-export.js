'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const token = localStorage.getItem('access_token');
  let busy = false;
  const headers = () => ({Authorization: `Bearer ${token}`});
  function status(text, error = false) { $('status').textContent = text; $('status').classList.toggle('error', error); }
  function setBusy(value) {
    busy = value;
    for (const id of ['preview', 'download', 'downloadExcel', 'showtime', 'mode', 'gate', 'conflicts']) $(id).disabled = value;
  }
  async function request(url) {
    const res = await fetch(url, {headers: headers(), cache: 'no-store'});
    if (!res.ok) {
      if (res.status === 401) { $('loginPrompt').hidden = false; throw Error('Phiên đăng nhập hết hạn. Vui lòng đăng nhập lại.'); }
      if (res.status === 403) throw Error('Bạn không có quyền xem hoặc xuất dữ liệu của suất diễn này.');
      const data = await res.json().catch(() => ({}));
      throw Error(data.message || `Không thể tải dữ liệu (${res.status}).`);
    }
    return res;
  }
  function url(action) {
    if (!$('showtime').value) throw Error('Vui lòng chọn suất diễn.');
    const params = new URLSearchParams({includeScanLog: String($('mode').value === 'audit'), onlyConflicts: String($('conflicts').checked)});
    if ($('gate').value.trim()) params.set('gateName', $('gate').value.trim());
    return `/api/shows/${encodeURIComponent($('showtime').value)}/checkins/${action}?${params}`;
  }
  async function preview() {
    if (busy) return;
    setBusy(true); $('results').hidden = true; status('Đang tải dữ liệu…');
    try {
      const data = await (await request(url('export-preview'))).json();
      $('summary').replaceChildren();
      for (const text of [`${data.admittedCount} lượt vào`, `${data.uniqueTickets} vé đã vào duy nhất`, `${data.conflictCount} dòng có xung đột`, `${data.totalRows} dòng xuất`]) {
        const el = document.createElement('span'); el.textContent = text; $('summary').append(el);
      }
      $('rows').replaceChildren();
      const fmt = value => value ? new Date(value).toLocaleString('vi-VN', {timeZone:'Asia/Ho_Chi_Minh', hour12:false}) : '';
      for (const item of data.items) {
        const row = document.createElement('tr');
        for (const value of [item.ticketCode,item.seatNumber,fmt(item.checkinTime),item.gateName,item.entryType,item.result,item.isConflict?'Có':'Không',item.conflictNote]) {
          const cell = document.createElement('td'); cell.textContent = value ?? ''; row.append(cell);
        }
        if (item.isConflict) row.children[6].className = 'conflict';
        $('rows').append(row);
      }
      $('previewNote').textContent = data.totalRows > data.previewLimit ? `Hiển thị ${data.previewLimit} dòng đầu. CSV chứa đầy đủ ${data.totalRows} dòng theo bộ lọc.` : `${data.totalRows} dòng theo bộ lọc.`;
      $('results').hidden = false;
      status(data.totalRows ? 'Đã tải dữ liệu. Bạn có thể tải CSV để lưu hồ sơ.' : 'Chưa có dữ liệu phù hợp với bộ lọc. CSV sẽ chỉ có tiêu đề.');
    } catch (err) { status(err.message, true); } finally { setBusy(false); }
  }
  $('exportForm').addEventListener('submit', e => { e.preventDefault(); preview(); });
  $('mode').addEventListener('change', () => {
    $('modeHelp').textContent = $('mode').value === 'audit' ? 'Bao gồm lượt vào thành công và lượt quét offline xung đột. Kết quả “Xung đột” không xác nhận khách đã vào cửa.' : 'Mỗi dòng là một lượt vào cửa thành công. Lượt vào lại được duyệt không tự động được coi là xung đột.';
  });
  for (const id of ['showtime','mode','gate','conflicts']) $(id).addEventListener('input', () => { $('results').hidden = true; status('Bộ lọc đã thay đổi. Chọn Xem trước hoặc Tải CSV.'); });
  async function downloadFile(excel) {
    if (busy) return;
    setBusy(true); status(excel ? 'Đang tạo file Excel…' : 'Đang tạo CSV…');
    try {
      const res = await request(url(excel ? 'export-excel' : 'export-csv')); const blob = await res.blob();
      const objectUrl = URL.createObjectURL(blob); const a = document.createElement('a');
      a.href = objectUrl; a.download = `checkin-${$('showtime').value}-${$('mode').value}.${excel ? 'xlsx' : 'csv'}`; document.body.append(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(objectUrl), 1000); status(excel ? 'Đã tải Excel với định dạng sẵn. Mở file .xlsx vừa tải.' : 'Đã tải CSV. Để mở bảng có định dạng sẵn, chọn Tải Excel (.xlsx).');
    } catch(err) { status(err.message, true); } finally { setBusy(false); }
  }
  $('download').addEventListener('click', () => downloadFile(false));
  $('downloadExcel').addEventListener('click', () => downloadFile(true));
  async function init() {
    if (!token) { $('showtime').replaceChildren(new Option('Vui lòng đăng nhập','')); $('loginPrompt').hidden = false; status('Cần đăng nhập để xuất dữ liệu.', true); setBusy(true); return; }
    try {
      const data = await (await request('/api/events')).json();
      $('showtime').replaceChildren(new Option('Chọn suất diễn',''));
      for (const event of data.data || []) for (const show of event.showtimes || []) {
        $('showtime').add(new Option(`${event.title} · ${show.name || new Date(show.startTime).toLocaleString('vi-VN',{timeZone:'Asia/Ho_Chi_Minh'})}`, show.id));
      }
      const preset = new URLSearchParams(location.search).get('showId');
      if (preset && [...$('showtime').options].some(o => o.value === preset)) { $('showtime').value = preset; await preview(); }
      else status('Chọn suất diễn để xem và xuất dữ liệu.');
    } catch(err) { status(err.message,true); $('showtime').replaceChildren(new Option('Không tải được suất diễn','')); }
  }
  init();
})();
