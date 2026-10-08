SELECT o."Id" AS "OrderId", o."Status", oi."Id" AS "TicketId", oi."IsCheckedIn" 
FROM orders o 
JOIN order_items oi ON o."Id" = oi."OrderId" 
WHERE o."Status" = 'Paid' AND oi."IsCheckedIn" = false
LIMIT 1;

SELECT o."Id" AS "OrderId", o."Status", oi."Id" AS "TicketId" 
FROM orders o 
JOIN order_items oi ON o."Id" = oi."OrderId" 
WHERE o."Status" != 'Paid' AND o."Status" != 'Pending' 
LIMIT 1;
