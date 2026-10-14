using System.Runtime.InteropServices;
using System.Security.Principal;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class UsersProvider : IInfoProvider
{
    public string Id => "users";
    public string Title => "Users";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 80;
    public string Icon => "\uE716";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);

        b.Try("Current user", () =>
        {
            using var id = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            // A UAC-filtered admin token reports "limited" elevation; the Administrators SID is then deny-only.
            var isAdmin = elevated || ElevationType(id.Token) == 3;
            b.Group("Current user")
                .Add("Name", id.Name)
                .Add("SID", id.User?.Value)
                .Flag("Administrator", isAdmin)
                .Flag("Elevated", elevated)
                .Add("Authentication", id.AuthenticationType)
                .Add("Profile folder", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                .Add("Logon server", Environment.GetEnvironmentVariable("LOGONSERVER")?.TrimStart('\\'));
        });

        b.Try("Local users", () =>
        {
            var t = b.Table("Local users", "Name", "Full name", "Disabled", "Password required", "Last logon", "Logons", "SID");
            foreach (var u in Wmi.Query("SELECT Name, FullName, Disabled, PasswordRequired, SID FROM Win32_UserAccount WHERE LocalAccount=True")
                         .OrderBy(u => u.Str("Name"), StringComparer.CurrentCultureIgnoreCase))
            {
                var name = u.Str("Name") ?? "";
                var (lastLogon, logons) = LogonInfo(name);
                t.Row(name, u.Str("FullName"), YesNo(u.Bool("Disabled")), YesNo(u.Bool("PasswordRequired")),
                    Fmt.DateTime(lastLogon), logons > 0 ? logons : null, u.Str("SID"));
            }
        });

        b.Try("Local groups", () =>
        {
            var t = b.Table("Local groups", "Name", "Description", "Members", "SID");
            foreach (var g in Wmi.Query("SELECT Name, Description, SID FROM Win32_Group WHERE LocalAccount=True")
                         .OrderBy(g => g.Str("Name"), StringComparer.CurrentCultureIgnoreCase))
            {
                var name = g.Str("Name") ?? "";
                t.Row(name, g.Str("Description"), string.Join(", ", GroupMembers(name)), g.Str("SID"));
            }
        });

        return b.Build();
    }

    private static string YesNo(bool? v) => v switch { true => "Yes", false => "No", null => "" };

    private static int ElevationType(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            return GetTokenInformation(token, TokenElevationTypeClass, buffer, 4, out _) ? Marshal.ReadInt32(buffer) : 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (DateTime? LastLogon, int Logons) LogonInfo(string user)
    {
        if (NetUserGetInfo(null, user, 2, out var buf) != 0 || buf == IntPtr.Zero) return (null, 0);
        try
        {
            var info = Marshal.PtrToStructure<UserInfo2>(buf);
            DateTime? last = info.LastLogon > 0 ? DateTimeOffset.FromUnixTimeSeconds(info.LastLogon).LocalDateTime : null;
            return (last, (int)info.NumLogons);
        }
        finally { NetApiBufferFree(buf); }
    }

    private static List<string> GroupMembers(string group)
    {
        var list = new List<string>();
        var resume = IntPtr.Zero;
        if (NetLocalGroupGetMembers(null, group, 3, out var buf, -1, out var read, out _, ref resume) != 0 || buf == IntPtr.Zero)
            return list;
        try
        {
            for (var i = 0; i < read; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size));
                if (!string.IsNullOrEmpty(name)) list.Add(name);
            }
        }
        finally { NetApiBufferFree(buf); }
        return list;
    }

    private const int TokenElevationTypeClass = 18;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo2
    {
        public IntPtr Name, Password;
        public uint PasswordAge, Priv;
        public IntPtr HomeDir, Comment;
        public uint Flags;
        public IntPtr ScriptPath;
        public uint AuthFlags;
        public IntPtr FullName, UsrComment, Parms, Workstations;
        public uint LastLogon, LastLogoff, AcctExpires, MaxStorage, UnitsPerWeek;
        public IntPtr LogonHours;
        public uint BadPwCount, NumLogons;
        public IntPtr LogonServer;
        public uint CountryCode, CodePage;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer,
        int maxLength, out int entriesRead, out int totalEntries, ref IntPtr resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
