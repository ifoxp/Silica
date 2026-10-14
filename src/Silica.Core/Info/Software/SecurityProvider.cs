using System.Runtime.InteropServices;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class SecurityProvider : IInfoProvider
{
    public string Id => "security";
    public string Title => "Security";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 70;
    public string Icon => "\uE72E";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var admin = SoftwareHelpers.IsElevated();
        var lm = Registry.LocalMachine;

        b.Try("Windows Defender", () =>
        {
            var r = Wmi.First("SELECT * FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender");
            if (r == null) return;
            var age = r.Long("AntivirusSignatureAge");
            b.Group("Microsoft Defender")
                .Word("Service", OnOff(r.Bool("AMServiceEnabled")))
                .Word("Antivirus", OnOff(r.Bool("AntivirusEnabled")))
                .Word("Real-time protection", OnOff(r.Bool("RealTimeProtectionEnabled")))
                .Word("Tamper protection", OnOff(r.Bool("IsTamperProtected")))
                .Add("Running mode", r.Str("AMRunningMode"))
                .Add("Signature version", r.Str("AntivirusSignatureVersion"))
                .Add("Signatures updated", Fmt.DateTime(ValidDate(r.Date("AntivirusSignatureLastUpdated"))))
                .Add("Signature age", age is >= 0 and < 10000 ? $"{age} {Fmt.Unit("d")}" : null)
                .Add("Engine version", r.Str("AMEngineVersion"))
                .Add("Product version", r.Str("AMProductVersion"))
                .Add("Last quick scan", Fmt.DateTime(ValidDate(r.Date("QuickScanEndTime"))))
                .Add("Last full scan", Fmt.DateTime(ValidDate(r.Date("FullScanEndTime"))));
        });

        b.Try("Security Center", () =>
        {
            var t = b.Table("Security products", "Type", "Name", "State", "Definitions");
            foreach (var (cls, kind) in (ReadOnlySpan<(string, string)>)[("AntiVirusProduct", "Antivirus"), ("AntiSpywareProduct", "Antispyware"), ("FirewallProduct", "Firewall")])
            {
                foreach (var r in Wmi.Query($"SELECT displayName, productState FROM {cls}", @"root\SecurityCenter2"))
                {
                    var state = r.Long("productState") ?? 0;
                    t.Row(kind, r.Str("displayName"), (state & 0x1000) != 0 ? "On" : "Off",
                        kind == "Firewall" ? "" : (state & 0x10) == 0 ? "Up to date" : "Out of date");
                }
            }
        });

        b.Try("Windows Firewall", () =>
        {
            const string policy = @"SOFTWARE\Policies\Microsoft\WindowsFirewall\";
            const string local = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
            var g = b.Group("Windows Firewall");
            foreach (var (label, policyName, localName) in (ReadOnlySpan<(string, string, string)>)[
                ("Domain profile", "DomainProfile", "DomainProfile"),
                ("Private profile", "PrivateProfile", "StandardProfile"),
                ("Public profile", "PublicProfile", "PublicProfile")])
            {
                var v = Reg.Long(lm, policy + policyName, "EnableFirewall") ?? Reg.Long(lm, local + localName, "EnableFirewall");
                g.Word(label, v switch { 1 => "On", 0 => "Off", _ => null });
            }
        });

        b.Try("System protection", () =>
        {
            const string uacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
            var lua = Reg.Long(lm, uacKey, "EnableLUA");
            var g = b.Group("System protection")
                .Word("User Account Control", lua == 0 ? "Off" : "On")
                .Word("UAC level", lua == 0 ? null : UacLevel(Reg.Long(lm, uacKey, "ConsentPromptBehaviorAdmin"), Reg.Long(lm, uacKey, "PromptOnSecureDesktop")));

            var secureBoot = Reg.Long(lm, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            g.Word("Secure Boot", secureBoot switch { 1 => "On", 0 => "Off", _ => "Not supported" });

            if (SoftwareHelpers.ReadDeviceGuard() is { } dg)
            {
                g.Word("Virtualization-based security", dg.Vbs)
                    .Word("Memory integrity (HVCI)", dg.Service(DeviceGuardState.Hvci))
                    .Word("Credential Guard", dg.Service(DeviceGuardState.CredentialGuard));
            }

            var ppl = Reg.Long(lm, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL");
            g.Word("LSA protection", ppl is 1 or 2 ? "On" : "Off");
            g.Word("SmartScreen", SmartScreen());
        });

        b.Try("TPM", () =>
        {
            var g = b.Group("TPM");
            // These namespaces need admin rights, and a denied query costs several seconds.
            var tpm = admin ? Wmi.First("SELECT * FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm") : null;
            if (tpm != null)
            {
                g.Flag("Present", true)
                    .Flag("Enabled", tpm.Bool("IsEnabled_InitialValue"))
                    .Flag("Activated", tpm.Bool("IsActivated_InitialValue"))
                    .Flag("Owned", tpm.Bool("IsOwned_InitialValue"))
                    .Add("Specification version", tpm.Str("SpecVersion")?.Split(',')[0].Trim())
                    .Add("Manufacturer", tpm.Str("ManufacturerIdTxt"))
                    .Add("Manufacturer version", tpm.Str("ManufacturerVersion"));
                return;
            }

            var info = new TpmDeviceInfo { StructVersion = 1 };
            var rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info);
            g.Flag("Present", rc == 0);
            if (rc == 0) g.Add("Specification version", info.TpmVersion switch { 2 => "2.0", 1 => "1.2", _ => null });
            if (!admin) b.Warn("TPM details (state, manufacturer) require administrator rights.");
        });

        b.Try("BitLocker", () => BitLocker(b, admin));

        return b.Build();
    }

    private static void BitLocker(PageBuilder b, bool admin)
    {
        var t = b.Table("BitLocker", "Drive", "Protection", "Status", "Encryption method");
        var volumes = !admin ? [] : Wmi.Query("SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod FROM Win32_EncryptableVolume",
            @"root\cimv2\Security\MicrosoftVolumeEncryption");
        if (volumes.Count > 0)
        {
            foreach (var v in volumes.Where(v => v.Str("DriveLetter") != null).OrderBy(v => v.Str("DriveLetter")))
            {
                t.Row(v.Str("DriveLetter"),
                    v.Int("ProtectionStatus") switch { 1 => "On", 0 => "Off", _ => "Unknown" },
                    v.Int("ConversionStatus") switch
                    {
                        0 => "Fully decrypted",
                        1 => "Fully encrypted",
                        2 => "Encryption in progress",
                        3 => "Decryption in progress",
                        4 => "Encryption paused",
                        5 => "Decryption paused",
                        _ => ""
                    },
                    v.Int("EncryptionMethod") switch
                    {
                        1 => "AES 128 with diffuser",
                        2 => "AES 256 with diffuser",
                        3 => "AES 128",
                        4 => "AES 256",
                        5 => "Hardware",
                        6 => "XTS-AES 128",
                        7 => "XTS-AES 256",
                        _ => ""
                    });
            }
            return;
        }

        // Without admin rights the shell still exposes a coarse per-volume BitLocker state.
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name).ToArray();
        if (SoftwareHelpers.TryRun(() => ShellBitLocker(drives), TimeSpan.FromSeconds(5), out var states, sta: true) && states != null)
        {
            foreach (var (drive, state) in states)
            {
                var (protection, status) = state switch
                {
                    1 => ("On", "Fully encrypted"),
                    2 => ("Off", "Fully decrypted"),
                    3 => ("Off", "Encryption in progress"),
                    4 => ("Off", "Decryption in progress"),
                    5 => ("Suspended", "Fully encrypted"),
                    6 => ("On", "Locked"),
                    8 => ("Off", "Waiting for activation"),
                    _ => ("", "")
                };
                if (protection.Length > 0) t.Row(drive.TrimEnd('\\'), protection, status, "");
            }
        }
        if (!admin) b.Warn("Detailed BitLocker status (encryption method) requires administrator rights.");
    }

    private static List<(string Drive, int State)> ShellBitLocker(string[] drives)
    {
        var list = new List<(string, int)>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return list;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var d in drives)
            {
                dynamic? folder = null, self = null;
                try
                {
                    folder = shell.NameSpace(d);
                    if (folder == null) continue;
                    self = folder.Self;
                    object? v = self.ExtendedProperty("System.Volume.BitLockerProtection");
                    if (v != null) list.Add((d, Convert.ToInt32(v)));
                }
                catch { }
                finally
                {
                    SoftwareHelpers.Release(self);
                    SoftwareHelpers.Release(folder);
                }
            }
        }
        finally { SoftwareHelpers.Release(shell); }
        return list;
    }

    private static string? OnOff(bool? v) => v switch { true => "On", false => "Off", null => null };

    private static DateTime? ValidDate(DateTime? d) => d is { Year: > 1601 } ? d : null;

    private static string UacLevel(long? consent, long? secureDesktop) => consent switch
    {
        0 => "Never notify",
        2 => "Always notify",
        5 when secureDesktop == 0 => "Notify, no dimming",
        5 or null => "Notify on app changes",
        1 or 3 => "Prompt for credentials",
        4 => "Prompt for consent",
        _ => "Custom"
    };

    private static string SmartScreen()
    {
        var policy = Reg.Long(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
        if (policy is { } p) return p == 0 ? "Off" : "On";
        var v = Reg.Str(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        return string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase) ? "Off" : "On";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, ref TpmDeviceInfo info);
}
