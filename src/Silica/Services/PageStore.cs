using System.Collections.Concurrent;
using Silica.Core.Info;
using Silica.Core.Model;

namespace Silica.Services;

/// <summary>Collects info pages on demand on background threads and caches them for the session.</summary>
public static class PageStore
{
    private static readonly ConcurrentDictionary<string, InfoPage> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<InfoPage>> Pending = new();

    /// <summary>Raised (on a worker thread) whenever a page has been collected.</summary>
    public static event Action<InfoPage>? Collected;

    public static InfoPage? Cached(string id) => Cache.GetValueOrDefault(id);

    /// <summary>Values carry units in the language they were read in, so a language switch re-reads pages.</summary>
    public static void Clear() => Cache.Clear();

    public static IEnumerable<InfoPage> All => Cache.Values;

    public static Task<InfoPage> GetAsync(IInfoProvider provider, bool refresh = false)
    {
        if (!refresh && Cache.TryGetValue(provider.Id, out var page)) return Task.FromResult(page);
        return Pending.GetOrAdd(provider.Id, _ => Run(provider));
    }

    private static Task<InfoPage> Run(IInfoProvider provider)
    {
        var tcs = new TaskCompletionSource<InfoPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var page = provider.Collect(CancellationToken.None);
                Cache[provider.Id] = page;
                tcs.SetResult(page);
                Collected?.Invoke(page);
            }
            catch (Exception ex)
            {
                Log.Error($"Page '{provider.Id}' failed", ex);
                var page = new InfoPage { Id = provider.Id, Title = provider.Title };
                page.Warnings.Add(ex.Message);
                tcs.SetResult(page);
            }
            finally
            {
                Pending.TryRemove(provider.Id, out _);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Collect " + provider.Id
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }
}
