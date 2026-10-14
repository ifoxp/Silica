namespace Silica.Core.Bench;

public static class Benchmarks
{
    private static readonly string[] PreferredOrder =
    [
        "mem-read", "mem-write", "mem-copy", "mem-latency",
        "cpu-queens", "cpu-zlib", "cpu-aes", "cpu-hash",
        "fpu-mandel", "fpu-julia", "fpu-nbody"
    ];

    public static IReadOnlyList<IBenchmark> All { get; } = Discover();

    public static IBenchmark? Find(string id) => All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IBenchmark[] Discover()
    {
        static int KindRank(BenchKind k) => k switch { BenchKind.Memory => 0, BenchKind.Cpu => 1, _ => 2 };
        static int Rank(string id) => Array.IndexOf(PreferredOrder, id) is var i and >= 0 ? i : int.MaxValue;

        return typeof(Benchmarks).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IBenchmark).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IBenchmark)Activator.CreateInstance(t)!)
            .OrderBy(b => KindRank(b.Kind))
            .ThenBy(b => Rank(b.Id))
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
