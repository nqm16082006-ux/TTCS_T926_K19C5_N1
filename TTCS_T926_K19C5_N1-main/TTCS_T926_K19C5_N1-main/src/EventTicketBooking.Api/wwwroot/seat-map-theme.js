const seatZonePalette = [
  { bg: '#fef3c7', border: '#d97706', ink: '#92400e', rank: 0 },
  { bg: '#dbeafe', border: '#2563eb', ink: '#1e40af', rank: 1 },
  { bg: '#ccfbf1', border: '#0d9488', ink: '#115e59', rank: 2 },
  { bg: '#ffe4e6', border: '#e11d48', ink: '#9f1239', rank: 3 },
  { bg: '#e0e7ff', border: '#6366f1', ink: '#3730a3', rank: 4 },
  { bg: '#ecfccb', border: '#65a30d', ink: '#3f6212', rank: 5 }
];
function getSeatTheme(categoryName) {
  const name = String(categoryName || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim();
  if (name.includes('vip')) return seatZonePalette[0];
  if (/^(hang|khu vuc hang|category) a\b/.test(name)) return seatZonePalette[1];
  if (/thuong|standard|tieu chuan/.test(name)) return seatZonePalette[2];
  let hash = 0;
  for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return seatZonePalette[3 + hash % 3];
}
function setSeatTheme(element, name) {
  const theme = getSeatTheme(name);
  element.style.setProperty('--zone-bg', theme.bg);
  element.style.setProperty('--zone-border', theme.border);
  element.style.setProperty('--zone-ink', theme.ink);
}
