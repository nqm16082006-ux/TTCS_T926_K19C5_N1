DO $$
DECLARE
    v_org_id uuid := '54ccfb79-4922-4d43-8ff3-c1b337fffe59';
    v_event1_id uuid := 'e1111111-1111-1111-1111-111111111111';
    v_showtime1_id uuid := 'a1111111-1111-1111-1111-111111111111';
    v_cat_standard uuid := 'c1111111-1111-1111-1111-111111111111';
    v_cat_vip uuid := 'c2222222-2222-2222-2222-222222222222';
    v_cat_vvip uuid := 'c3333333-3333-3333-3333-333333333333';

    v_event2_id uuid := 'e2222222-2222-2222-2222-222222222222';
    v_showtime2_id uuid := 'a2222222-2222-2222-2222-222222222222';
    v_cat_fanzone uuid := 'c4444444-4444-4444-4444-444444444444';
    v_cat_phothong uuid := 'c5555555-5555-5555-5555-555555555555';

    v_row text;
    v_num int;
    v_target_cat uuid;
BEGIN
    -- Chỉ seed nếu chưa có event nào
    IF NOT EXISTS (SELECT 1 FROM "Events") THEN
        -- 1. Event 1
        INSERT INTO "Events" ("Id", "Title", "Description", "Location", "StartTime", "EndTime", "TotalSeats", "CreatedAt", "UpdatedAt", "OwnerId")
        VALUES (
            v_event1_id,
            'Live Concert Anh Trai Say Hi 2026',
            'Đêm nhạc quy tụ dàn ca sĩ hàng đầu với hệ thống âm thanh, ánh sáng chuẩn quốc tế đỉnh cao.',
            'Sân vận động Quốc gia Mỹ Đình, Hà Nội',
            NOW() + interval '7 days',
            NOW() + interval '7 days' + interval '4 hours',
            50,
            NOW(),
            NOW(),
            v_org_id
        );

        INSERT INTO "Showtimes" ("Id", "EventId", "StartTime", "EndTime", "AvailableSeats", "Status")
        VALUES (
            v_showtime1_id,
            v_event1_id,
            NOW() + interval '7 days',
            NOW() + interval '7 days' + interval '4 hours',
            50,
            'OnSale'::showtime_status
        );

        INSERT INTO "SeatCategories" ("Id", "ShowtimeId", "Name", "Price") VALUES
        (v_cat_standard, v_showtime1_id, 'Standard / Vé Thường', 300000),
        (v_cat_vip, v_showtime1_id, 'VIP / Vé Cao Cấp', 1200000),
        (v_cat_vvip, v_showtime1_id, 'VVIP / Vé Đặc Biệt', 2500000);

        -- Seats for Showtime 1 (A, B -> VVIP; C -> VIP; D, E -> Standard)
        FOREACH v_row IN ARRAY ARRAY['A', 'B', 'C', 'D', 'E']
        LOOP
            FOR v_num IN 1..10
            LOOP
                IF v_row IN ('A', 'B') THEN
                    v_target_cat := v_cat_vvip;
                ELSIF v_row = 'C' THEN
                    v_target_cat := v_cat_vip;
                ELSE
                    v_target_cat := v_cat_standard;
                END IF;

                INSERT INTO "Seats" ("Id", "ShowtimeId", "SeatCategoryId", "Row", "SeatNumber", "Status")
                VALUES (gen_random_uuid(), v_showtime1_id, v_target_cat, v_row, v_num, 'AVAILABLE');
            END LOOP;
        END LOOP;

        -- 2. Event 2
        INSERT INTO "Events" ("Id", "Title", "Description", "Location", "StartTime", "EndTime", "TotalSeats", "CreatedAt", "UpdatedAt", "OwnerId")
        VALUES (
            v_event2_id,
            'Festival Âm Nhạc Mùa Hè 2026',
            'Lễ hội âm nhạc mùa hè cuồng nhiệt với nhiều nghệ sĩ Indie và Rock bùng nổ.',
            'Phố đi bộ Nguyễn Huệ, Quận 1, TP. Hồ Chí Minh',
            NOW() + interval '14 days',
            NOW() + interval '14 days' + interval '5 hours',
            50,
            NOW(),
            NOW(),
            v_org_id
        );

        INSERT INTO "Showtimes" ("Id", "EventId", "StartTime", "EndTime", "AvailableSeats", "Status")
        VALUES (
            v_showtime2_id,
            v_event2_id,
            NOW() + interval '14 days',
            NOW() + interval '14 days' + interval '5 hours',
            50,
            'OnSale'::showtime_status
        );

        INSERT INTO "SeatCategories" ("Id", "ShowtimeId", "Name", "Price") VALUES
        (v_cat_fanzone, v_showtime2_id, 'Vé Fan Zone', 800000),
        (v_cat_phothong, v_showtime2_id, 'Vé Phổ Thông', 200000);

        -- Seats for Showtime 2 (A, B -> Fan Zone; C, D, E -> Phổ Thông)
        FOREACH v_row IN ARRAY ARRAY['A', 'B', 'C', 'D', 'E']
        LOOP
            FOR v_num IN 1..10
            LOOP
                IF v_row IN ('A', 'B') THEN
                    v_target_cat := v_cat_fanzone;
                ELSE
                    v_target_cat := v_cat_phothong;
                END IF;

                INSERT INTO "Seats" ("Id", "ShowtimeId", "SeatCategoryId", "Row", "SeatNumber", "Status")
                VALUES (gen_random_uuid(), v_showtime2_id, v_target_cat, v_row, v_num, 'AVAILABLE');
            END LOOP;
        END LOOP;

        RAISE NOTICE 'Seed 2 events and 100 seats successfully completed!';
    ELSE
        RAISE NOTICE 'Events already exist. Skipping seed.';
    END IF;
END $$;
