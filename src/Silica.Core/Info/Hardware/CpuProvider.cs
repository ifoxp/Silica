using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class CpuProvider : IInfoProvider
{
    public string Id => "cpu";
    public string Title => "Processor";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 10;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var cpu = Wmi.First("SELECT Name, Manufacturer, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, " +
                            "MaxClockSpeed, ExtClock, AddressWidth, VirtualizationFirmwareEnabled, " +
                            "SecondLevelAddressTranslationExtensions FROM Win32_Processor");
        var id = Cpuid.Read();
        var topo = Topology.Read();

        b.Try("Processor", () =>
        {
            var g = b.Group(id?.Brand ?? cpu?.Str("Name") ?? "Processor", localizeTitle: false);
            g.Add("Vendor", VendorName(id?.Vendor ?? cpu?.Str("Manufacturer")));
            g.Add("Socket", cpu?.Str("SocketDesignation"));
            if (topo.Packages > 1) g.Add("Packages", topo.Packages);
            g.Add("Cores", topo.Cores > 0 ? topo.Cores : cpu?.Int("NumberOfCores"));
            g.Add("Threads", topo.Threads > 0 ? topo.Threads : cpu?.Int("NumberOfLogicalProcessors"));
            if (topo.IsHybrid)
            {
                g.Add("Performance cores", topo.PerformanceCores);
                g.Add("Efficiency cores", topo.EfficiencyCores);
            }
            g.Flag("Hybrid architecture", topo.IsHybrid);
            g.Add("Base clock", Fmt.Mhz(id?.BaseMhz is > 0 ? id.BaseMhz : cpu?.Long("MaxClockSpeed")));
            g.Add("Max clock", Fmt.Mhz(id?.MaxMhz));
            g.Add("Bus clock", Fmt.Mhz(id?.BusMhz is > 0 ? id.BusMhz : cpu?.Long("ExtClock")));
            g.Add("Address width", cpu?.Int("AddressWidth") is { } w ? $"{w}-bit" : null);
        });

        if (id != null)
        {
            b.Try("CPUID", () =>
            {
                var g = b.Group("CPUID");
                g.Add("Vendor string", id.Vendor);
                g.Add("Brand string", id.Brand);
                g.Add("Family", HexDec(id.Family));
                g.Add("Model", HexDec(id.Model));
                g.Add("Stepping", HexDec(id.Stepping));
                g.Add("Signature", Fmt.Hex(id.Signature, 8));
                var (rev, sig) = Microcode();
                g.Add("Microcode revision", rev);
                g.Add("Microcode signature", sig);
            });
        }

        b.Try("Cache", () =>
        {
            var t = b.Table("Cache", "Level", "Type", "Size", "Instances", "Total", "Associativity", "Line size", "Shared by");
            foreach (var (c, count) in topo.Caches)
            {
                t.Row($"L{c.Level}", c.Type, Fmt.Bytes(c.Size), count, Fmt.Bytes((double)c.Size * count),
                    c.Associativity == 0xFF ? "Fully associative" : $"{c.Associativity}-way",
                    $"{c.LineSize} B", c.SharedBy == 1 ? "1 thread" : $"{c.SharedBy} threads");
            }
        });

        b.Try("Virtualization", () =>
        {
            var hv = Wmi.First("SELECT HypervisorPresent FROM Win32_ComputerSystem")?.Bool("HypervisorPresent");
            var g = b.Group("Virtualization");
            g.Add("Technology", id == null ? null : id.Vendor == "AuthenticAMD" ? "AMD-V (SVM)" : "Intel VT-x (VMX)");
            g.Flag("Supported by CPU", id?.VirtualizationCapable);
            // Under a running hypervisor WMI reports these as false; in the Hyper-V root partition they are known to be on.
            if (id?.HypervisorRoot == true) g.Word("Enabled in firmware", "Enabled");
            else if (hv != true)
            {
                g.Word("Enabled in firmware", Enabled(cpu?.Bool("VirtualizationFirmwareEnabled")));
                g.Flag("SLAT", cpu?.Bool("SecondLevelAddressTranslationExtensions"));
            }
            g.Flag("Hypervisor present", hv ?? id?.Hypervisor);
            g.Add("Hypervisor", id?.HypervisorVendor);
        });

        if (id != null)
        {
            b.Try("Instruction sets", () =>
            {
                var t = b.Table("Instruction sets", "Feature", "Supported");
                foreach (var (name, supported) in id.Features) t.Row(name, supported ? "Yes" : "No");
            });
        }
        else b.Warn("CPUID is not available on this processor architecture");

        return b.Build();
    }

    private static string HexDec(int v) => $"{Fmt.Hex((ulong)v)} ({v})";

    private static string? Enabled(bool? v) => v switch { true => "Enabled", false => "Disabled", _ => null };

    private static string? VendorName(string? vendor) => vendor switch
    {
        "GenuineIntel" => "Intel",
        "AuthenticAMD" => "AMD",
        "CentaurHauls" => "Centaur",
        "  Shanghai  " => "Zhaoxin",
        "HygonGenuine" => "Hygon",
        _ => vendor
    };

    private static (string? Revision, string? Signature) Microcode()
    {
        const string key = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        return (Dword(Reg.Get(Registry.LocalMachine, key, "Update Revision")),
                Dword(Reg.Get(Registry.LocalMachine, key, "Update Signature")));

        // Intel stores an 8-byte value with the revision in the high dword; AMD uses the low (or only) dword.
        static string? Dword(object? v)
        {
            if (v is not byte[] { Length: >= 4 } b) return null;
            var hi = b.Length >= 8 ? BitConverter.ToUInt32(b, 4) : 0;
            var value = hi != 0 ? hi : BitConverter.ToUInt32(b, 0);
            return value == 0 ? null : Fmt.Hex(value);
        }
    }

    internal sealed record CacheInfo(int Level, string Type, long Size, int Associativity, int LineSize, int SharedBy);

    internal sealed class Topology
    {
        public int Packages { get; private set; }
        public int Cores { get; private set; }
        public int Threads { get; private set; }
        public int PerformanceCores { get; private set; }
        public int EfficiencyCores { get; private set; }
        public bool IsHybrid => PerformanceCores > 0 && EfficiencyCores > 0;
        public List<(CacheInfo Info, int Count)> Caches { get; private set; } = [];

        public static Topology Read()
        {
            var t = new Topology();
            var cores = new List<(int Class, int Threads)>();
            var caches = new Dictionary<CacheInfo, int>();
            foreach (var (rel, d) in Native.LogicalProcessorInfo(0xFFFF))
            {
                switch (rel)
                {
                    case 0: // RelationProcessorCore: Flags, EfficiencyClass, Reserved[20], GroupCount, GroupMask[]
                        cores.Add((d[1], Native.CountThreads(d, 22, 24)));
                        break;
                    case 3:
                        t.Packages++;
                        break;
                    case 2: // RelationCache: Level, Associativity, LineSize, CacheSize, Type, Reserved[18], GroupCount, GroupMask[]
                        var type = BitConverter.ToInt32(d, 8) switch { 0 => "Unified", 1 => "Instruction", 2 => "Data", 3 => "Trace", _ => "" };
                        var info = new CacheInfo(d[0], type, BitConverter.ToUInt32(d, 4), d[1], BitConverter.ToUInt16(d, 2),
                            Native.CountThreads(d, 30, 32));
                        caches[info] = caches.GetValueOrDefault(info) + 1;
                        break;
                }
            }
            t.Cores = cores.Count;
            t.Threads = cores.Sum(c => c.Threads);
            if (cores.Count > 0 && cores.Select(c => c.Class).Distinct().Count() > 1)
            {
                var top = cores.Max(c => c.Class);
                t.PerformanceCores = cores.Count(c => c.Class == top);
                t.EfficiencyCores = cores.Count - t.PerformanceCores;
            }
            t.Caches = caches.Select(kv => (kv.Key, kv.Value))
                .OrderBy(c => c.Key.Level).ThenBy(c => c.Key.Type, StringComparer.Ordinal).ThenByDescending(c => c.Key.Size)
                .ToList();
            return t;
        }
    }

    internal sealed class Cpuid
    {
        public string Vendor { get; private init; } = "";
        public string? Brand { get; private init; }
        public uint Signature { get; private init; }
        public int Family { get; private init; }
        public int Model { get; private init; }
        public int Stepping { get; private init; }
        public long BaseMhz { get; private init; }
        public long MaxMhz { get; private init; }
        public long BusMhz { get; private init; }
        public bool Hypervisor { get; private init; }
        public string? HypervisorVendor { get; private init; }
        public bool HypervisorRoot { get; private init; }
        public bool VirtualizationCapable { get; private init; }
        public List<(string Name, bool Supported)> Features { get; } = [];

        public static Cpuid? Read()
        {
            if (!X86Base.IsSupported) return null;

            var (maxLeaf, vb, vc, vd) = X86Base.CpuId(0, 0);
            var vendor = Ascii(vb, vd, vc);
            var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
            var l1 = Leaf(1, 0, maxLeaf);
            var l7 = Leaf(7, 0, maxLeaf);
            var l71 = Leaf(7, 1, maxLeaf);
            var l16 = Leaf(0x16, 0, maxLeaf);
            var e1 = ExtLeaf(1, maxExt);

            string? brand = null;
            if ((uint)maxExt >= 0x80000004)
            {
                var sb = new StringBuilder();
                for (var i = 2; i <= 4; i++)
                {
                    var (a, b2, c, d) = X86Base.CpuId(unchecked((int)(0x80000000 + i)), 0);
                    sb.Append(Ascii(a, b2, c, d));
                }
                brand = sb.ToString().TrimEnd('\0').Trim();
                if (brand.Length == 0) brand = null;
            }

            var sig = (uint)l1.A;
            var baseFamily = (int)((sig >> 8) & 0xF);
            var baseModel = (int)((sig >> 4) & 0xF);
            var family = baseFamily == 0xF ? baseFamily + (int)((sig >> 20) & 0xFF) : baseFamily;
            var model = baseFamily is 0x6 or 0xF ? (int)(((sig >> 16) & 0xF) << 4) + baseModel : baseModel;
            var amd = vendor is "AuthenticAMD" or "HygonGenuine";

            // Leaves 0x40000000+ are only defined when the hypervisor-present bit is set.
            string? hvVendor = null;
            var hvRoot = false;
            if (Bit(l1.C, 31))
            {
                var (hvMax, hb, hc, hd) = X86Base.CpuId(0x40000000, 0);
                var hvId = Ascii(hb, hc, hd).TrimEnd('\0');
                hvVendor = hvId switch
                {
                    "Microsoft Hv" => "Microsoft Hyper-V",
                    "KVMKVMKVM" => "KVM",
                    "VMwareVMware" => "VMware",
                    "VBoxVBoxVBox" => "VirtualBox",
                    "XenVMMXenVMM" => "Xen",
                    "TCGTCGTCGTCG" => "QEMU",
                    " lrpepyh  vr" => "Parallels",
                    _ => hvId.Trim().Length > 0 ? hvId.Trim() : null
                };
                // HV_PARTITION_PRIVILEGE_MASK.CreatePartitions is only granted to the root partition.
                hvRoot = hvId == "Microsoft Hv" && (uint)hvMax >= 0x40000003 && Bit(X86Base.CpuId(0x40000003, 0).Ebx, 0);
            }
            var virt = (amd ? Bit(e1.C, 2) : Bit(l1.C, 5)) || hvRoot;

            var r = new Cpuid
            {
                Vendor = vendor,
                Brand = brand,
                Signature = sig,
                Family = family,
                Model = model,
                Stepping = (int)(sig & 0xF),
                BaseMhz = l16.A & 0xFFFF,
                MaxMhz = l16.B & 0xFFFF,
                BusMhz = l16.C & 0xFFFF,
                Hypervisor = Bit(l1.C, 31),
                HypervisorVendor = hvVendor,
                HypervisorRoot = hvRoot,
                VirtualizationCapable = virt
            };

            var f = r.Features;
            f.Add(("MMX", Bit(l1.D, 23)));
            f.Add(("SSE", Bit(l1.D, 25)));
            f.Add(("SSE2", Bit(l1.D, 26)));
            f.Add(("SSE3", Bit(l1.C, 0)));
            f.Add(("SSSE3", Bit(l1.C, 9)));
            f.Add(("SSE4.1", Bit(l1.C, 19)));
            f.Add(("SSE4.2", Bit(l1.C, 20)));
            if (amd) f.Add(("SSE4A", Bit(e1.C, 6)));
            f.Add(("AVX", Bit(l1.C, 28)));
            f.Add(("AVX2", Bit(l7.B, 5)));
            f.Add(("FMA3", Bit(l1.C, 12)));
            if (amd) f.Add(("FMA4", Bit(e1.C, 16)));
            f.Add(("AVX-VNNI", Bit(l71.A, 4)));
            f.Add(("AVX-512F", Bit(l7.B, 16)));
            f.Add(("AVX-512DQ", Bit(l7.B, 17)));
            f.Add(("AVX-512CD", Bit(l7.B, 28)));
            f.Add(("AVX-512BW", Bit(l7.B, 30)));
            f.Add(("AVX-512VL", Bit(l7.B, 31)));
            f.Add(("AVX-512IFMA", Bit(l7.B, 21)));
            f.Add(("AVX-512VBMI", Bit(l7.C, 1)));
            f.Add(("AVX-512VBMI2", Bit(l7.C, 6)));
            f.Add(("AVX-512VNNI", Bit(l7.C, 11)));
            f.Add(("AVX-512BITALG", Bit(l7.C, 12)));
            f.Add(("AVX-512VPOPCNTDQ", Bit(l7.C, 14)));
            f.Add(("AVX-512BF16", Bit(l71.A, 5)));
            f.Add(("AVX-512FP16", Bit(l7.D, 23)));
            f.Add(("AES-NI", Bit(l1.C, 25)));
            f.Add(("PCLMULQDQ", Bit(l1.C, 1)));
            f.Add(("VAES", Bit(l7.C, 9)));
            f.Add(("VPCLMULQDQ", Bit(l7.C, 10)));
            f.Add(("GFNI", Bit(l7.C, 8)));
            f.Add(("SHA", Bit(l7.B, 29)));
            f.Add(("RDRAND", Bit(l1.C, 30)));
            f.Add(("RDSEED", Bit(l7.B, 18)));
            f.Add(("BMI1", Bit(l7.B, 3)));
            f.Add(("BMI2", Bit(l7.B, 8)));
            f.Add(("ADX", Bit(l7.B, 19)));
            f.Add(("POPCNT", Bit(l1.C, 23)));
            f.Add(("LZCNT", Bit(e1.C, 5)));
            f.Add(("F16C", Bit(l1.C, 29)));
            f.Add(("MOVBE", Bit(l1.C, 22)));
            f.Add(("CMPXCHG16B", Bit(l1.C, 13)));
            f.Add(("XSAVE", Bit(l1.C, 26)));
            f.Add(("RDTSCP", Bit(e1.D, 27)));
            f.Add(("x86-64", Bit(e1.D, 29)));
            f.Add(("NX", Bit(e1.D, 20)));
            f.Add((amd ? "AMD-V (SVM)" : "VT-x (VMX)", r.VirtualizationCapable));
            f.Add(("Hybrid", Bit(l7.D, 15)));
            if (Bit(l7.B, 4) || Bit(l7.B, 11))
            {
                f.Add(("TSX-HLE", Bit(l7.B, 4)));
                f.Add(("TSX-RTM", Bit(l7.B, 11)));
            }
            if (Bit(l7.D, 24))
            {
                f.Add(("AMX-TILE", true));
                f.Add(("AMX-INT8", Bit(l7.D, 25)));
                f.Add(("AMX-BF16", Bit(l7.D, 22)));
            }
            return r;
        }

        private static (int A, int B, int C, int D) Leaf(int leaf, int sub, int max) =>
            leaf <= max ? X86Base.CpuId(leaf, sub) : default;

        private static (int A, int B, int C, int D) ExtLeaf(int index, int maxExt) =>
            (uint)(0x80000000 + index) <= (uint)maxExt ? X86Base.CpuId(unchecked((int)(0x80000000 + index)), 0) : default;

        private static bool Bit(int reg, int bit) => ((reg >> bit) & 1) != 0;

        private static string Ascii(params int[] regs)
        {
            var bytes = new byte[regs.Length * 4];
            for (var i = 0; i < regs.Length; i++) BitConverter.GetBytes(regs[i]).CopyTo(bytes, i * 4);
            return Encoding.ASCII.GetString(bytes);
        }
    }
}
