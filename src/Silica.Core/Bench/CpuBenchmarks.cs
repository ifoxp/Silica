using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Silica.Core.Bench;

public sealed class CpuQueensBenchmark : IBenchmark
{
    private const int N = 14;
    private static readonly (int A, int B)[] Prefixes = BuildPrefixes();

    public string Id => "cpu-queens";
    public string Title => "CPU Queens";
    public string Description => "Integer and branching speed, solving the 14 queens puzzle";
    public BenchKind Kind => BenchKind.Cpu;
    public string Unit => "points";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        long next = -1;
        var (solutions, seconds) = BenchRunner.Run(ctx, ctx.Threads, _ => () =>
        {
            // Work units are the valid placements of the first two rows, handed out round-robin.
            var (a, b) = Prefixes[(int)(Interlocked.Increment(ref next) % Prefixes.Length)];
            int all = (1 << N) - 1, cols = a | b;
            return Solve(all, cols, ((a << 1 | b) << 1) & all, ((a >> 1 | b) >> 1));
        });
        return BenchRunner.Result(this, ctx.Threads, solutions / seconds / 1000, sw,
            $"N = {N}, {solutions / seconds / 1e6:0.00} M solutions/s, {BenchRunner.Threads(ctx.Threads)}");
    }

    internal static long Solve(int all, int cols, int ld, int rd)
    {
        if (cols == all) return 1;
        long count = 0;
        var free = all & ~(cols | ld | rd);
        while (free != 0)
        {
            var bit = free & -free;
            free ^= bit;
            count += Solve(all, cols | bit, ((ld | bit) << 1) & all, (rd | bit) >> 1);
        }
        return count;
    }

    private static (int, int)[] BuildPrefixes()
    {
        var list = new List<(int, int)>();
        for (var i = 0; i < N; i++)
            for (var j = 0; j < N; j++)
                if (Math.Abs(i - j) > 1) list.Add((1 << i, 1 << j));
        return [.. list];
    }
}

public sealed class CpuCompressionBenchmark : IBenchmark
{
    private const int Block = 1 << 20;
    private const int CorpusBlocks = 16;
    private static readonly Lazy<byte[]> Corpus = new(BuildCorpus);

    public string Id => "cpu-zlib";
    public string Title => "CPU Compression";
    public string Description => "Compresses text-like data with zlib";
    public BenchKind Kind => BenchKind.Cpu;
    public string Unit => "MB/s";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var corpus = Corpus.Value;
        long packed = 0, unpacked = 0;
        var (bytes, seconds) = BenchRunner.Run(ctx, ctx.Threads, t =>
        {
            var input = corpus.AsSpan().ToArray();
            var output = new MemoryStream(Block + Block / 2);
            var k = t;
            return () =>
            {
                output.SetLength(0);
                using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(input, k++ % CorpusBlocks * Block, Block);
                Interlocked.Add(ref packed, output.Length);
                Interlocked.Add(ref unpacked, Block);
                return Block;
            };
        });
        var ratio = packed > 0 ? (double)unpacked / packed : 0;
        return BenchRunner.Result(this, ctx.Threads, bytes / seconds / 1e6, sw,
            $"ratio {ratio:0.00}:1, {BenchRunner.Threads(ctx.Threads)}");
    }

    /// <summary>Pseudo-text: Zipf-like word frequencies, sentences, numbers and line breaks; compresses roughly 3:1.</summary>
    private static byte[] BuildCorpus()
    {
        var rng = new Rng(0xC0FFEE);
        var words = new string[4096];
        const string letters = "etaoinshrdlcumwfgypbvkjxqz";
        var sb = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            sb.Clear();
            var len = 2 + rng.Below(4) + rng.Below(6);
            for (var c = 0; c < len; c++) sb.Append(letters[(int)(letters.Length * Math.Pow(rng.NextDouble(), 1.6))]);
            words[i] = sb.ToString();
        }

        var data = new byte[Block * CorpusBlocks];
        var pos = 0;
        var startSentence = true;
        while (pos < data.Length)
        {
            string token;
            var roll = rng.Below(100);
            if (roll < 3) token = rng.Below(100000).ToString();
            else token = words[(int)(words.Length * Math.Pow(rng.NextDouble(), 3))];
            if (startSentence) token = char.ToUpperInvariant(token[0]) + token[1..];
            startSentence = false;
            roll = rng.Below(100);
            token += roll switch
            {
                < 7 => ". ",
                < 10 => ", ",
                < 11 => ".\r\n",
                _ => " "
            };
            if (roll < 7 || roll == 10) startSentence = true;
            pos += Encoding.ASCII.GetBytes(token.AsSpan(0, Math.Min(token.Length, data.Length - pos)), data.AsSpan(pos));
        }
        return data;
    }
}

public sealed class CpuAesBenchmark : IBenchmark
{
    private const int Chunk = 1 << 20;
    private const int ChunksPerStep = 4;

    public string Id => "cpu-aes";
    public string Title => "CPU AES";
    public string Description => "Encrypts data with AES-256 using hardware instructions";
    public BenchKind Kind => BenchKind.Cpu;
    public string Unit => "MB/s";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var ciphers = new List<AesGcm>();
        try
        {
            var (bytes, seconds) = BenchRunner.Run(ctx, ctx.Threads, t =>
            {
                var rng = new Rng((ulong)t + 1);
                var key = new byte[32];
                var plain = new byte[Chunk];
                var cipher = new byte[Chunk];
                var tag = new byte[16];
                var nonce = new byte[12];
                foreach (ref var b in key.AsSpan()) b = (byte)rng.Next();
                foreach (ref var b in plain.AsSpan()) b = (byte)rng.Next();
                var aes = new AesGcm(key, tag.Length);
                lock (ciphers) ciphers.Add(aes);
                ulong counter = 0;
                return () =>
                {
                    for (var i = 0; i < ChunksPerStep; i++)
                    {
                        BitConverter.TryWriteBytes(nonce, ++counter);
                        aes.Encrypt(nonce, plain, cipher, tag);
                    }
                    return ChunksPerStep * Chunk;
                };
            });
            return BenchRunner.Result(this, ctx.Threads, bytes / seconds / 1e6, sw,
                $"AES-256-GCM, {(System.Runtime.Intrinsics.X86.Aes.IsSupported ? "AES-NI" : "no AES-NI")}, {BenchRunner.Threads(ctx.Threads)}");
        }
        finally
        {
            foreach (var c in ciphers) c.Dispose();
        }
    }
}

public sealed class CpuHashBenchmark : IBenchmark
{
    private const int Chunk = 1 << 20;
    private const int ChunksPerStep = 4;

    public string Id => "cpu-hash";
    public string Title => "CPU Hash";
    public string Description => "Computes SHA-256 checksums of data";
    public BenchKind Kind => BenchKind.Cpu;
    public string Unit => "MB/s";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var (bytes, seconds) = BenchRunner.Run(ctx, ctx.Threads, t =>
        {
            var rng = new Rng((ulong)t + 7);
            var data = new byte[Chunk];
            foreach (ref var b in data.AsSpan()) b = (byte)rng.Next();
            var hash = new byte[32];
            return () =>
            {
                for (var i = 0; i < ChunksPerStep; i++)
                {
                    SHA256.HashData(data, hash);
                    data[i] ^= hash[0];
                }
                return ChunksPerStep * Chunk;
            };
        });
        return BenchRunner.Result(this, ctx.Threads, bytes / seconds / 1e6, sw,
            $"SHA-256, 1 MiB blocks, {BenchRunner.Threads(ctx.Threads)}");
    }
}
