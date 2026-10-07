/**
 * organizer-nav.js
 * Thống nhất logic xác thực, hiển thị thông tin người dùng,
 * và điều hướng dùng chung cho toàn bộ các trang của Ban Tổ Chức (Organizer):
 * - Tổng quan (overview.html)
 * - Quản lý sự kiện (events.html)
 * - Tạo sự kiện mới (create-event.html)
 */

(function () {
  const token = localStorage.getItem('access_token');
  const userInfoRaw = localStorage.getItem('user_info');

  // Kiểm tra đăng nhập
  if (!token) {
    window.location.href = './login.html?returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search);
    return;
  }

  function init() {
    initOrganizerHeaderAndSidebar();
    highlightActiveNav();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  function initOrganizerHeaderAndSidebar() {
    let name = 'Event Organizer';
    let role = 'Lead Organizer';

    if (userInfoRaw) {
      try {
        const user = JSON.parse(userInfoRaw);
        const roles = (user.roles || []).map(r => (r || '').toLowerCase());
        const canAccess = roles.includes('organizer') || roles.includes('staff') || roles.includes('admin');

        if (!canAccess) {
          alert('Bạn không có quyền truy cập khu vực Ban tổ chức.');
          window.location.href = './public-events.html';
          return;
        }

        name = user.fullName || user.username || user.email || 'Event Organizer';
        role = roles.includes('admin') ? 'Quản Trị Viên (Admin)' : 'Lead Organizer';
      } catch (e) {
        console.error('Error parsing user_info:', e);
      }
    }

    // Cập nhật tên đơn vị quản trị & người dùng trên Sidebar và Header
    const orgElems = [
      document.getElementById('sidebarOrgName'),
      document.getElementById('topHeaderOrg'),
      document.getElementById('topUserName')
    ];
    orgElems.forEach(el => {
      if (el) el.textContent = name;
    });

    const roleElem = document.getElementById('topUserRole');
    if (roleElem) roleElem.textContent = role;

    const firstChar = name.trim().charAt(0).toUpperCase() || 'E';
    const avatarElem = document.getElementById('topUserAvatar');
    if (avatarElem) {
      avatarElem.textContent = firstChar;
    }
  }

  const ACTIVE_CLASS = 'bg-primary-container text-on-primary font-label-lg text-label-lg rounded-lg shadow-sm';
  const INACTIVE_CLASS = 'text-on-surface-variant hover:bg-surface-container hover:text-on-surface transition-all font-label-lg text-label-lg';

  function getNavElements() {
    return {
      'overview': document.getElementById('nav-tong-quan'),
      'events': document.getElementById('nav-quan-ly-su-kien'),
      'create': document.getElementById('nav-tao-su-kien'),
      'showtimes': document.getElementById('nav-danh-sach-suat-dien'),
      'seats': document.getElementById('nav-cau-hinh-ghe')
    };
  }

  function highlightActiveNav() {
    const path = window.location.pathname.toLowerCase();
    const search = window.location.search.toLowerCase();
    const navItems = getNavElements();

    // Reset tất cả về inactive trước
    Object.values(navItems).forEach(el => {
      if (el) {
        el.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${INACTIVE_CLASS}`;
      }
    });

    // Kích hoạt theo trang hiện tại & query parameter
    if (path.includes('create-event') || path.includes('tao-su-kien')) {
      if (navItems.create) {
        navItems.create.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${ACTIVE_CLASS}`;
      }
    } else if (path.includes('overview') || path.includes('tong-quan')) {
      if (navItems.overview) {
        navItems.overview.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${ACTIVE_CLASS}`;
      }
    } else {
      // events.html hoặc các action liên quan
      if (search.includes('action=showtimes') && navItems.showtimes) {
        navItems.showtimes.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${ACTIVE_CLASS}`;
      } else if (search.includes('action=seats') && navItems.seats) {
        navItems.seats.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${ACTIVE_CLASS}`;
      } else if (navItems.events) {
        navItems.events.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${ACTIVE_CLASS}`;
      }
    }
  }

  // Hàm chuyển trạng thái active thủ công khi mở/đóng modal trên events.html
  window.setActiveNav = function (key) {
    const navItems = getNavElements();
    Object.values(navItems).forEach(el => {
      if (el) el.className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${INACTIVE_CLASS}`;
    });
    if (navItems[key]) {
      navItems[key].className = `flex items-center gap-space-sm px-3 py-2.5 rounded-lg ${ACTIVE_CLASS}`;
    }
  };

  // Hàm đăng xuất dùng chung
  window.logout = function () {
    if (confirm('Bạn có chắc chắn muốn đăng xuất không?')) {
      localStorage.clear();
      window.location.href = './login.html';
    }
  };
})();
