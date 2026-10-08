CREATE TABLE "AuditLogs" (
    "Id" uuid NOT NULL,
    "ActorUserId" uuid NOT NULL,
    "ActorUsername" character varying(50) NOT NULL,
    "TargetUserId" uuid NOT NULL,
    "OldRoles" character varying(500) NOT NULL,
    "NewRoles" character varying(500) NOT NULL,
    "Action" character varying(50) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_AuditLogs" PRIMARY KEY ("Id")
);


CREATE TABLE payment_events (
    "Id" uuid NOT NULL,
    "TransactionId" character varying(100) NOT NULL,
    "RawPayload" text NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_payment_events" PRIMARY KEY ("Id")
);


CREATE TABLE "Roles" (
    "Id" uuid NOT NULL,
    "Name" character varying(50) NOT NULL,
    "Description" character varying(255),
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_Roles" PRIMARY KEY ("Id")
);


CREATE TABLE "Users" (
    "Id" uuid NOT NULL,
    "Username" character varying(50) NOT NULL,
    "Email" character varying(100) NOT NULL,
    "PasswordHash" character varying(255) NOT NULL,
    "FullName" character varying(100),
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "IsActive" boolean NOT NULL DEFAULT FALSE,
    "VerificationCode" text,
    "VerificationCodeExpiresAt" timestamp with time zone,
    CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
);


CREATE TABLE "Events" (
    "Id" uuid NOT NULL,
    "OwnerId" uuid NOT NULL,
    "Title" character varying(250) NOT NULL,
    "Description" text,
    "Location" character varying(500) NOT NULL,
    "ImageUrl" text,
    "StartTime" timestamp with time zone NOT NULL,
    "EndTime" timestamp with time zone NOT NULL,
    "TotalSeats" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_Events" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Events_Users_OwnerId" FOREIGN KEY ("OwnerId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);


CREATE TABLE "UserRoles" (
    "UserId" uuid NOT NULL,
    "RoleId" uuid NOT NULL,
    "AssignedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_UserRoles" PRIMARY KEY ("UserId", "RoleId"),
    CONSTRAINT "FK_UserRoles_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_UserRoles_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);


CREATE TABLE "Showtimes" (
    "Id" uuid NOT NULL,
    "EventId" uuid NOT NULL,
    "StartTime" timestamp with time zone NOT NULL,
    "EndTime" timestamp with time zone NOT NULL,
    "AvailableSeats" integer NOT NULL,
    "Status" text NOT NULL,
    CONSTRAINT "PK_Showtimes" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Showtimes_Events_EventId" FOREIGN KEY ("EventId") REFERENCES "Events" ("Id") ON DELETE CASCADE
);


CREATE TABLE orders (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "ShowtimeId" uuid NOT NULL,
    "Status" text NOT NULL,
    "TotalAmount" integer NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_orders" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_orders_Showtimes_ShowtimeId" FOREIGN KEY ("ShowtimeId") REFERENCES "Showtimes" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_orders_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);


CREATE TABLE "SeatCategories" (
    "Id" uuid NOT NULL,
    "ShowtimeId" uuid NOT NULL,
    "Name" character varying(100) NOT NULL,
    "Price" integer,
    CONSTRAINT "PK_SeatCategories" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_SeatCategories_Showtimes_ShowtimeId" FOREIGN KEY ("ShowtimeId") REFERENCES "Showtimes" ("Id") ON DELETE CASCADE
);


CREATE TABLE payment_transactions (
    "Id" uuid NOT NULL,
    "OrderId" uuid NOT NULL,
    "OrderCode" bigint NOT NULL,
    "Amount" integer NOT NULL,
    "PaymentUrl" character varying(1000),
    "TransactionId" character varying(100),
    "Status" character varying(20) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    CONSTRAINT "PK_payment_transactions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_payment_transactions_orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES orders ("Id") ON DELETE CASCADE
);


CREATE TABLE "Seats" (
    "Id" uuid NOT NULL,
    "ShowtimeId" uuid NOT NULL,
    "SeatCategoryId" uuid NOT NULL,
    "Row" character varying(10) NOT NULL,
    "SeatNumber" integer NOT NULL,
    "Status" text NOT NULL,
    CONSTRAINT "PK_Seats" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Seats_SeatCategories_SeatCategoryId" FOREIGN KEY ("SeatCategoryId") REFERENCES "SeatCategories" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Seats_Showtimes_ShowtimeId" FOREIGN KEY ("ShowtimeId") REFERENCES "Showtimes" ("Id") ON DELETE CASCADE
);


CREATE TABLE order_items (
    "Id" uuid NOT NULL,
    "OrderId" uuid NOT NULL,
    "SeatId" uuid NOT NULL,
    "Price" integer NOT NULL,
    "IsCheckedIn" boolean NOT NULL,
    "CheckInGate" text,
    "CheckInTime" timestamp with time zone,
    CONSTRAINT "PK_order_items" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_order_items_Seats_SeatId" FOREIGN KEY ("SeatId") REFERENCES "Seats" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_order_items_orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES orders ("Id") ON DELETE CASCADE
);


CREATE TABLE seat_holds (
    "Id" uuid NOT NULL,
    "SeatId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Status" character varying(20) NOT NULL DEFAULT 'ACTIVE',
    "HeldAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "ExpiresAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_seat_holds" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_seat_holds_Seats_SeatId" FOREIGN KEY ("SeatId") REFERENCES "Seats" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_seat_holds_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);


CREATE INDEX "IX_AuditLogs_TargetUserId" ON "AuditLogs" ("TargetUserId");


CREATE INDEX "IX_Events_OwnerId" ON "Events" ("OwnerId");


CREATE UNIQUE INDEX "IX_order_items_OrderId_SeatId" ON order_items ("OrderId", "SeatId");


CREATE INDEX "IX_order_items_SeatId" ON order_items ("SeatId");


CREATE INDEX "IX_orders_ShowtimeId" ON orders ("ShowtimeId");


CREATE UNIQUE INDEX "IX_orders_UserId_ShowtimeId_Pending" ON orders ("UserId", "ShowtimeId") WHERE "Status" = 'Pending';


CREATE UNIQUE INDEX "IX_payment_events_TransactionId_Unique" ON payment_events ("TransactionId");


CREATE UNIQUE INDEX "IX_payment_transactions_OrderCode_Unique" ON payment_transactions ("OrderCode");


CREATE UNIQUE INDEX "IX_payment_transactions_OrderId_Unique" ON payment_transactions ("OrderId");


CREATE UNIQUE INDEX "IX_Roles_Name" ON "Roles" ("Name");


CREATE INDEX "IX_seat_holds_expires_at" ON seat_holds ("ExpiresAt");


CREATE UNIQUE INDEX "IX_seat_holds_SeatId_Active" ON seat_holds ("SeatId") WHERE "Status" = 'ACTIVE';


CREATE INDEX "IX_seat_holds_status_expires_at" ON seat_holds ("Status", "ExpiresAt");


CREATE INDEX "IX_seat_holds_UserId" ON seat_holds ("UserId");


CREATE INDEX "IX_SeatCategories_ShowtimeId" ON "SeatCategories" ("ShowtimeId");


CREATE INDEX "IX_Seats_SeatCategoryId" ON "Seats" ("SeatCategoryId");


CREATE INDEX "IX_Seats_ShowtimeId" ON "Seats" ("ShowtimeId");


CREATE UNIQUE INDEX "IX_Seats_ShowtimeId_Row_SeatNumber" ON "Seats" ("ShowtimeId", "Row", "SeatNumber");


CREATE INDEX "IX_Showtimes_EventId" ON "Showtimes" ("EventId");


CREATE INDEX "IX_UserRoles_RoleId" ON "UserRoles" ("RoleId");


CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");


CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");


