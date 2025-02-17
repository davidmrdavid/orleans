using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Internal;

namespace Orleans.Runtime.GrainDirectory
{
    internal class AdaptiveGrainDirectoryCache : IGrainDirectoryCache
    {
        internal class GrainDirectoryCacheEntry
        {
            internal IReadOnlyList<Tuple<SiloAddress, ActivationId>> Value { get; private set; }

            internal DateTime Created { get; set; }

            private DateTime LastRefreshed { get; set; }

            internal TimeSpan ExpirationTimer { get; private set; }

            internal int ETag { get; private set; }

            /// <summary>
            /// flag notifying whether this cache entry was accessed lately 
            /// (more precisely, since the last refresh)
            /// </summary>
            internal int NumAccesses { get; set; }

            internal GrainDirectoryCacheEntry(IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int etag, DateTime created, TimeSpan expirationTimer)
            {
                Value = value;
                ETag = etag;
                ExpirationTimer = expirationTimer;
                Created = created;
                LastRefreshed = DateTime.UtcNow;
                NumAccesses = 0;
            }

            internal bool IsExpired()
            {
                return DateTime.UtcNow >= LastRefreshed.Add(ExpirationTimer);
            }

            internal void Refresh(TimeSpan newExpirationTimer)
            {
                LastRefreshed = DateTime.UtcNow;
                ExpirationTimer = newExpirationTimer;
            }
        }

        private readonly LRU<GrainId, GrainDirectoryCacheEntry> cache;
        /// controls the time the new entry is considered "fresh" (unit: ms)
        private readonly TimeSpan initialExpirationTimer;
        /// controls the exponential growth factor (i.e., x2, x4) for the freshness timer (unit: none)
        private readonly double exponentialTimerGrowth;
        // controls the boundary on the expiration timer
        private readonly TimeSpan maxExpirationTimer;

        internal long NumAccesses;   // number of cache item accesses (for stats)
        internal long NumHits;       // number of cache access hits (for stats)

        internal long LastNumAccesses;
        internal long LastNumHits;

        public AdaptiveGrainDirectoryCache(TimeSpan initialExpirationTimer, TimeSpan maxExpirationTimer, double exponentialTimerGrowth, int maxCacheSize)
        {
            cache = new LRU<GrainId, GrainDirectoryCacheEntry>(maxCacheSize, TimeSpan.MaxValue, null);

            this.initialExpirationTimer = initialExpirationTimer;
            this.maxExpirationTimer = maxExpirationTimer;
            this.exponentialTimerGrowth = exponentialTimerGrowth;

            IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_CACHE_SIZE, () => cache.Count);
        }

        public void AddOrUpdate(GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int version)
        {            
            var entry = new GrainDirectoryCacheEntry(value, version, DateTime.UtcNow, initialExpirationTimer);

            // Notice that LRU should know how to throw the oldest entry if the cache is full
            cache.Add(key, entry);
        }

        public bool Remove(GrainId key)
        {
            GrainDirectoryCacheEntry tmp;
            return cache.RemoveKey(key, out tmp);
        }

        public void Clear()
        {
            cache.Clear();
        }

        public bool LookUp(GrainId key, out IReadOnlyList<Tuple<SiloAddress, ActivationId>> result, out int version)
        {
            result = default(IReadOnlyList<Tuple<SiloAddress, ActivationId>>);
            version = default(int);
            NumAccesses++;      // for stats

            // Here we do not check whether the found entry is expired. 
            // It will be done by the thread managing the cache.
            // This is to avoid situation where the entry was just expired, but the manager still have not run and have not refereshed it.
            GrainDirectoryCacheEntry tmp;
            if (!cache.TryGetValue(key, out tmp)) return false;

            NumHits++;      // for stats
            tmp.NumAccesses++;
            result = tmp.Value;
            version = tmp.ETag;
            return true;
        }

        public IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> KeyValues
        {
            get
            {
                var result = new List<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>>();
                IEnumerator<KeyValuePair<GrainId, GrainDirectoryCacheEntry>> enumerator = GetStoredEntries();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    result.Add(new Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>(current.Key, current.Value.Value, current.Value.ETag));
                }
                return result;
            }
        }

        public bool MarkAsFresh(GrainId key)
        {
            GrainDirectoryCacheEntry result;
            if (!cache.TryGetValue(key, out result)) return false;

            TimeSpan newExpirationTimer = StandardExtensions.Min(maxExpirationTimer, result.ExpirationTimer.Multiply(exponentialTimerGrowth));
            result.Refresh(newExpirationTimer);

            return true;
        }

        internal GrainDirectoryCacheEntry Get(GrainId key)
        {
            return cache.Get(key);
        }


        internal IEnumerator<KeyValuePair<GrainId, GrainDirectoryCacheEntry>> GetStoredEntries()
        {
            return cache.GetEnumerator();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            long curNumAccesses = NumAccesses - LastNumAccesses;
            LastNumAccesses = NumAccesses;
            long curNumHits = NumHits - LastNumHits;
            LastNumHits = NumHits;

            sb.Append("Adaptive cache statistics:").AppendLine();
            sb.AppendFormat("   Cache size: {0} entries ({1} maximum)", cache.Count, cache.MaximumSize).AppendLine();
            sb.AppendFormat("   Since last call:").AppendLine();
            sb.AppendFormat("      Accesses: {0}", curNumAccesses);
            sb.AppendFormat("      Hits: {0}", curNumHits);
            if (curNumAccesses > 0)
            {
                sb.AppendFormat("      Hit Rate: {0:F1}%", (100.0 * curNumHits) / curNumAccesses).AppendLine();
            }
            sb.AppendFormat("   Since start:").AppendLine();
            sb.AppendFormat("      Accesses: {0}", LastNumAccesses);
            sb.AppendFormat("      Hits: {0}", LastNumHits);
            if (LastNumAccesses > 0)
            {
                sb.AppendFormat("      Hit Rate: {0:F1}%", (100.0 * LastNumHits) / LastNumAccesses).AppendLine();
            }

            return sb.ToString();
        }

        public bool Remove(ActivationAddress activationAddress)
        {
            return cache.TryRemove(activationAddress.Grain, ActivationAddressEqual, activationAddress.Activation);

            static bool ActivationAddressEqual(ActivationId activationId, GrainDirectoryCacheEntry entry)
            {
                // entry.Value should always be == 1, but to be safe ask to remove the entry if the count is zero
                if (entry.Value.Count == 0 || activationId.Equals(entry.Value[0].Item2))
                {
                    return true;
                }
                return false;
            }
        }
    }
}
