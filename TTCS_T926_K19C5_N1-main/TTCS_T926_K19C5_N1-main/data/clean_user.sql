DELETE FROM "UserRoles" WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "Email" = 'nqm16082006@gmail.com');
DELETE FROM "Users" WHERE "Email" = 'nqm16082006@gmail.com';
