using System;
class Program {
    static void Main() {
        var vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var nowUtc = DateTime.UtcNow;
        var nowVn = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, vnTimeZone);
        var todayVnStartUtc = TimeZoneInfo.ConvertTimeToUtc(nowVn.Date, vnTimeZone);
        var tomorrowVnStartUtc = todayVnStartUtc.AddDays(1);
        Console.WriteLine($"todayVnStartUtc.Kind = {todayVnStartUtc.Kind}");
        Console.WriteLine($"tomorrowVnStartUtc.Kind = {tomorrowVnStartUtc.Kind}");
    }
}
