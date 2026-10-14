using System.Globalization;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// How the conversation says when a message was written, as Codex does: "14:08" under a message from today and
/// "29 Sep, 14:08" under an older one; over a message that starts the conversation or follows a pause, the day too —
/// "Today, 14:08", "Yesterday, 09:12", "Wednesday, 15:54", "Tue, 29 Sep at 12:09". The app runs without culture data
/// (invariant globalization): day and month names are the app's language's own strings.
/// </summary>
public static class MessageTimes
{
    /// <summary>A pause long enough that the next message gets its day and time over it.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromHours(1);

    /// <summary>The clock's "now" (tests set it).</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    private static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] ShortDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>The time on a message's line: "14:08" today, "29 Sep, 14:08" before; empty when unknown.</summary>
    public static string Short(DateTimeOffset? at)
    {
        if (at is not { } t) return "";
        var local = t.ToLocalTime();
        var time = Clock(local);
        return local.Date == Now().Date ? time : Loc.F("{0}, {1}", Date(local, year: false), time);
    }

    /// <summary>The day and time over a message: "Today, 14:08", "Wednesday, 15:54", "Tue, 29 Sep at 12:09".</summary>
    public static string Day(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = Now().Date;
        var time = Clock(local);
        var days = (today - local.Date).TotalDays;
        if (days == 0) return Loc.F("Today, {0}", time);
        if (days == 1) return Loc.F("Yesterday, {0}", time);
        if (days is > 1 and < 7) return Loc.F("{0}, {1}", Loc.T(Days[(int)local.DayOfWeek]), time);
        return Loc.F("{0}, {1} at {2}", Loc.T(ShortDays[(int)local.DayOfWeek]), Date(local, year: local.Year != today.Year), time);
    }

    private static string Clock(DateTimeOffset local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"29 Sep", or with its year "6 Oct 2025".</summary>
    private static string Date(DateTimeOffset local, bool year) =>
        string.Create(CultureInfo.InvariantCulture, $"{local.Day} {Loc.T(Months[local.Month - 1])}") + (year ? string.Create(CultureInfo.InvariantCulture, $" {local.Year}") : "");
}
