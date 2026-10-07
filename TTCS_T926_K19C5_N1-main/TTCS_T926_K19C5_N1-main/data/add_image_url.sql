ALTER TABLE "Events" ADD COLUMN IF NOT EXISTS "ImageUrl" text;
UPDATE "Events" SET "ImageUrl" = 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=1200&q=80' WHERE "ImageUrl" IS NULL OR "ImageUrl" = '';
