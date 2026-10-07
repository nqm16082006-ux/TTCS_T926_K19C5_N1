function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
}

function safeLocalReturnUrl(value) {
  try {
    const url = new URL(value, window.location.href);
    return url.origin === window.location.origin && ['http:', 'https:'].includes(url.protocol)
      ? url.pathname + url.search + url.hash : null;
  } catch {
    return null;
  }
}
