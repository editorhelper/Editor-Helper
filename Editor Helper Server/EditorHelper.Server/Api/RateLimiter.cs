using System.Collections.Concurrent;

namespace EditorHelper.Server.Api;

/// <summary>
/// Скользящее окно на адрес клиента: сколько запросов разрешено за минуту.
/// Без этого любой скрипт может залить сервер запросами каталога или, что хуже,
/// перебирать одноразовые коды входа.
///
/// Минута делится на 12 интервалов по 5 секунд. Суммируются только интервалы
/// не старше минуты, поэтому всплеск перестаёт влиять на решение ровно через
/// минуту, а не скачком в начале следующей.
/// </summary>
public sealed class RateLimiter
{
    private const int Buckets = 12;
    private static readonly TimeSpan BucketSpan = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(BucketSpan.TotalSeconds * Buckets);

    private readonly ConcurrentDictionary<string, Counters> _counters = new(StringComparer.Ordinal);
    private long _lastCleanupTicks;

    public sealed record Decision(bool Allowed, int RetryAfterSeconds, int Remaining);

    /// <summary>Засчитывает один запрос и говорит, пропускать ли его.</summary>
    public Decision Allow(string key, int limitPerMinute, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var slot = SlotIndex(timestamp);
        var counters = _counters.GetOrAdd(key, _ => new Counters());

        lock (counters)
        {
            var index = (int)(slot % Buckets);

            // Интервал уже принадлежит другому отрезку времени: обнуляем его,
            // иначе счётчик минутной давности сложился бы с текущим
            if (counters.Slots[index] != slot)
            {
                counters.Slots[index] = slot;
                counters.Counts[index] = 0;
            }

            counters.LastSeen = timestamp;

            var total = CountWithinWindow(counters, slot);
            if (total >= limitPerMinute)
            {
                var oldest = OldestCountedSlot(counters, slot);
                var retryAfter = oldest == null
                    ? Window.TotalSeconds
                    : Math.Max(1, Window.TotalSeconds - (timestamp - SlotTime(oldest.Value)).TotalSeconds);

                return new Decision(false, (int)Math.Ceiling(retryAfter), 0);
            }

            counters.Counts[index]++;
            return new Decision(true, 0, Math.Max(0, limitPerMinute - total - 1));
        }
    }

    /// <summary>
    /// Забытые адреса вычищаются: словарь не должен расти бесконечно, если сервер
    /// доступен из интернета. Обход делается не чаще раза в минуту.
    /// </summary>
    public void CleanupExpired(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var ticks = timestamp.UtcTicks;
        if (ticks - Interlocked.Read(ref _lastCleanupTicks) < TimeSpan.TicksPerMinute) return;
        Interlocked.Exchange(ref _lastCleanupTicks, ticks);

        foreach (var pair in _counters)
        {
            DateTimeOffset lastSeen;
            lock (pair.Value) lastSeen = pair.Value.LastSeen;
            if (timestamp - lastSeen > TimeSpan.FromMinutes(5)) _counters.TryRemove(pair.Key, out _);
        }
    }

    private static int CountWithinWindow(Counters counters, long slot)
    {
        var total = 0;
        for (int i = 0; i < Buckets; i++)
        {
            var age = slot - counters.Slots[i];
            if (age >= 0 && age < Buckets) total += counters.Counts[i];
        }
        return total;
    }

    /// <summary>
    /// Самый старый учтённый интервал: он первым выпадет из окна и освободит
    /// место, поэтому по нему считается Retry-After.
    /// </summary>
    private static long? OldestCountedSlot(Counters counters, long slot)
    {
        long? oldest = null;
        for (int i = 0; i < Buckets; i++)
        {
            if (counters.Counts[i] <= 0) continue;
            var age = slot - counters.Slots[i];
            if (age < 0 || age >= Buckets) continue;
            if (oldest == null || counters.Slots[i] < oldest) oldest = counters.Slots[i];
        }
        return oldest;
    }

    private static long SlotIndex(DateTimeOffset timestamp) =>
        timestamp.ToUnixTimeSeconds() / (long)BucketSpan.TotalSeconds;

    private static DateTimeOffset SlotTime(long slot) =>
        DateTimeOffset.FromUnixTimeSeconds(slot * (long)BucketSpan.TotalSeconds);

    private sealed class Counters
    {
        public readonly int[] Counts = new int[Buckets];
        public readonly long[] Slots = new long[Buckets];
        public DateTimeOffset LastSeen = DateTimeOffset.UtcNow;
    }
}
