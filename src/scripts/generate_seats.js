const fs = require('fs');

/**
 * T-21: Script sinh dữ liệu 2000 ghế mẫu cho Event Ticket Booking
 * Chạy bằng: node scripts/generate_seats.js
 */

const SEATS_COUNT = 2000;
const SEATS_PER_ROW = 50;
const OUTPUT_FILE = './data/sample_seats_2000.json';

// Cấu hình các loại ghế
const CATEGORIES = [
    { name: "VIP", ratio: 0.1, price: 1500000 },
    { name: "Standard", ratio: 0.6, price: 500000 },
    { name: "Economy", ratio: 0.3, price: 200000 }
];

function generateSeats() {
    const seats = [];
    const numRows = Math.ceil(SEATS_COUNT / SEATS_PER_ROW);

    let currentIndex = 1;
    
    for (let r = 0; r < numRows; r++) {
        // Sinh tên hàng: A, B, C... Z, AA, AB...
        let rowName = getRowName(r);

        for (let s = 1; s <= SEATS_PER_ROW; s++) {
            if (currentIndex > SEATS_COUNT) break;

            // Xác định Category dựa trên vị trí (Hàng đầu là VIP, giữa Standard, cuối Economy)
            let categoryIndex = 0;
            let progress = currentIndex / SEATS_COUNT;
            
            if (progress > CATEGORIES[0].ratio + CATEGORIES[1].ratio) {
                categoryIndex = 2;
            } else if (progress > CATEGORIES[0].ratio) {
                categoryIndex = 1;
            }

            seats.push({
                row: rowName,
                seatNumber: s,
                categoryName: CATEGORIES[categoryIndex].name
            });

            currentIndex++;
        }
    }

    return seats;
}

// Chuyển index 0,1,2 thành A, B, C...
function getRowName(index) {
    let name = '';
    let temp = index;
    while (temp >= 0) {
        name = String.fromCharCode((temp % 26) + 65) + name;
        temp = Math.floor(temp / 26) - 1;
    }
    return name;
}

const data = generateSeats();
console.log(`Đã sinh thành công ${data.length} ghế.`);

// Đảm bảo thư mục data tồn tại
if (!fs.existsSync('./data')){
    fs.mkdirSync('./data');
}

fs.writeFileSync(OUTPUT_FILE, JSON.stringify(data, null, 2));
console.log(`Đã lưu file tại: ${OUTPUT_FILE}`);
