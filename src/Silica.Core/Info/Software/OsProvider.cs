using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class OsProvider : IInfoProvider
{
    private const string NtKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public string Id => "os";
    public string Title => "Operating system";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 0;
    public string Icon => "\uE7F8";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var lm = Registry.LocalMachine;
        var cs = Wmi.First("SELECT PartOfDomain, Domain, Workgroup, HypervisorPresent FROM Win32_ComputerSystem");
        var build = (int)(Reg.Long(lm, NtKey, "CurrentBuildNumber") ?? Environment.OSVersion.Version.Build);

        b.Try("Operating system", () =>
        {
            var name = Reg.Str(lm, NtKey, "ProductName");
            if (name != null && build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            var ubr = Reg.Long(lm, NtKey, "UBR");
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

            b.Group("Operating system")
                .Add("Product name", name)
                .Add("Edition", Reg.Str(lm, NtKey, "EditionID"))
                .Add("Version", Reg.Str(lm, NtKey, "DisplayVersion") ?? Reg.Str(lm, NtKey, "ReleaseId"))
                .Add("Build", ubr is { } u ? $"{build}.{u}" : build.ToString(CultureInfo.InvariantCulture))
                .Add("Architecture", Arch(RuntimeInformation.OSArchitecture))
                .Add("Kernel version", KernelVersion())
                .Add("DirectX", Environment.OSVersion.Version.Major >= 10 ? "DirectX 12" : null)
                .Add("Last boot", Fmt.DateTime(DateTime.Now - uptime))
                .Add("Uptime", Fmt.Duration(uptime));
        });

        b.Try("Installation", () =>
        {
            var installed = Reg.Long(lm, NtKey, "InstallDate") is { } secs and > 0
                ? DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime
                : (DateTime?)null;
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            b.Group("Installation")
                .Add("Install date", Fmt.DateTime(installed))
                .Add("Registered owner", Reg.Str(lm, NtKey, "RegisteredOwner"))
                .Add("Registered organization", Reg.Str(lm, NtKey, "RegisteredOrganization"))
                .Add("Product ID", Reg.Str(lm, NtKey, "ProductId"))
                .Add("Windows folder", windir)
                .Add("System drive", Path.GetPathRoot(windir)?.TrimEnd('\\'));
        });

        b.Try("Computer", () =>
        {
            var g = b.Group("Computer")
                .Add("Computer name", Environment.MachineName);
            if (cs?.Bool("PartOfDomain") == true) g.Add("Domain", cs.Str("Domain"));
            else g.Add("Workgroup", cs?.Str("Workgroup") ?? cs?.Str("Domain"));
            g.Add("Current user", $@"{Environment.UserDomainName}\{Environment.UserName}")
                .Flag("Elevated", SoftwareHelpers.IsElevated())
                .Add("UI language", CultureLabel(CultureInfo.CurrentUICulture))
                .Add("Locale", CultureLabel(CultureInfo.CurrentCulture))
                .Add("Time zone", TimeZoneInfo.Local.DisplayName);
        });

        b.Try("Virtualization", () =>
        {
            var g = b.Group("Virtualization and security");
            if (SoftwareHelpers.ReadDeviceGuard() is { } dg)
            {
                g.Word("Virtualization-based security", dg.Vbs)
                    .Word("Memory integrity (HVCI)", dg.Service(DeviceGuardState.Hvci));
            }
            g.Flag("Hypervisor present", cs?.Bool("HypervisorPresent"));
            var dev = Reg.Long(lm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense");
            g.Word("Developer mode", dev == 1 ? "Enabled" : "Disabled");
        });

        b.Try(".NET", () =>
        {
            b.Group(".NET")
                .Add(".NET Framework", NetFramework(Reg.Long(lm, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release")));

            var t = b.Table(".NET runtimes", "Runtime", "Version", "Architecture");
            var roots = new List<(string Dir, string Arch)>
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Arch(RuntimeInformation.OSArchitecture))
            };
            if (Environment.Is64BitOperatingSystem)
                roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "x86"));

            foreach (var (dir, arch) in roots)
            foreach (var fx in (string[])["Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App"])
            {
                var path = Path.Combine(dir, "dotnet", "shared", fx);
                if (!Directory.Exists(path)) continue;
                var versions = Directory.GetDirectories(path).Select(Path.GetFileName)
                    .OrderByDescending(v => Version.TryParse(v?.Split('-')[0], out var x) ? x : new Version());
                foreach (var v in versions) t.Row(fx, v, arch);
            }
        });

        return b.Build();
    }

    private static string KernelVersion()
    {
        // The numeric fields are subject to the OS version lie for unmanifested processes; the string is not.
        var fvi = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe"));
        var text = fvi.FileVersion?.Split(' ')[0];
        return Version.TryParse(text, out var v) ? v.ToString() : $"{fvi.ProductMajorPart}.{fvi.ProductMinorPart}.{fvi.ProductBuildPart}.{fvi.ProductPrivatePart}";
    }

    internal static string Arch(Architecture a) => a switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "ARM64",
        Architecture.Arm => "ARM",
        _ => a.ToString()
    };

    internal static string CultureLabel(CultureInfo c) => $"{c.DisplayName} ({c.Name})";

    private static string? NetFramework(long? release) => release switch
    {
        null => null,
        >= 533320 => "4.8.1",
        >= 528040 => "4.8",
        >= 461808 => "4.7.2",
        >= 461308 => "4.7.1",
        >= 460798 => "4.7",
        >= 394802 => "4.6.2",
        >= 394254 => "4.6.1",
        >= 393295 => "4.6",
        >= 379893 => "4.5.2",
        >= 378675 => "4.5.1",
        _ => "4.5"
    } + (release is { } r ? " " + Fmt.T("(release {0})", r) : "");
}
