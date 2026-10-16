function formatEventDescription(value) {
  const escape = text => String(text ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  let description = String(value ?? '').trim();
  if (!description) return '<p>Chưa có thông tin giới thiệu sự kiện.</p>';
  let metadata = '';
  const match = description.match(/\s*\[Thẻ:\s*([^\]]*)\]\s*\[Thể loại:\s*([^\]]*)\]\s*$/);
  if (match) {
    description = description.slice(0, match.index).trim();
    const tags = match[1].trim().split(/\s+/).filter(Boolean);
    metadata = '<div class="event-description-tags">' + tags.map(tag => `<span>${escape(tag)}</span>`).join('') + '</div>';
  }
  const allowed = new Set(['P', 'BR', 'UL', 'OL', 'LI', 'STRONG', 'B', 'EM', 'I', 'U', 'H2', 'H3', 'H4', 'BLOCKQUOTE', 'A']);
  const blocked = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'SVG', 'MATH', 'TEMPLATE', 'FORM', 'INPUT', 'BUTTON', 'IMG', 'VIDEO', 'AUDIO']);
  const document = new DOMParser().parseFromString(description, 'text/html');
  const hasBlockContent = !!document.body.querySelector('p, ul, ol, li, h2, h3, h4, blockquote');
  function render(node) {
    if (node.nodeType === 3) {
      // Source indentation is ordinary HTML whitespace, not a visible line break.
      // Keep explicit newlines for descriptions entered as plain text.
      if (!hasBlockContent) return escape(node.textContent).replace(/\r?\n/g, '<br>');
      const text = node.textContent.replace(/\s+/g, ' ');
      if (!text.trim() && ['BODY', 'UL', 'OL'].includes(node.parentNode.tagName)) return '';
      return escape(text);
    }
    if (node.nodeType !== 1 || blocked.has(node.tagName)) return '';
    const children = Array.from(node.childNodes).map(render).join('');
    if (!allowed.has(node.tagName)) return children;
    const tag = node.tagName.toLowerCase();
    if (tag === 'br') return '<br>';
    if (tag === 'p' && !node.textContent.replace(/\u00a0/g, ' ').trim() && !node.querySelector('a')) return '';
    if (tag === 'a') {
      try {
        const url = new URL(node.getAttribute('href'));
        if (url.protocol === 'https:' || url.protocol === 'http:')
          return `<a href="${escape(url.href)}" target="_blank" rel="noopener noreferrer">${children}</a>`;
      } catch {}
      return children;
    }
    // Retain the editor's heading appearance without trusting arbitrary classes or attributes.
    const heading = tag === 'p' && node.classList.contains('font-bold') ? ' class="event-description-heading"' : '';
    return `<${tag}${heading}>${children}</${tag}>`;
  }
  return '<div class="event-rich-description">' + Array.from(document.body.childNodes).map(render).join('') + metadata + '</div>';
}
