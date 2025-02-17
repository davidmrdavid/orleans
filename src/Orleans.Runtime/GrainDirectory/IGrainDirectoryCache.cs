using System;
using System.Collections.Generic;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    interface IGrainDirectoryCache
    {
        /// <summary>
        /// Adds a new entry with the given version into the cache: key (grain) --> value
        /// The new entry will override any existing entry under the given key, 
        /// regardless of the stored version
        /// </summary>
        /// <param name="key">key to add</param>
        /// <param name="value">value to add</param>
        /// <param name="version">version for the value</param>
        void AddOrUpdate(GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> value, int version);

        /// <summary>
        /// Removes an entry from the cache given its key
        /// </summary>
        /// <param name="key">key to remove</param>
        /// <returns>True if the entry was in the cache and the removal was successful</returns>
        bool Remove(GrainId key);

        /// <summary>
        /// Removes an entry from the cache given its key
        /// </summary>
        /// <param name="key">key to remove</param>
        /// <returns>True if the entry was in the cache and the removal was successful</returns>
        bool Remove(ActivationAddress key);

        /// <summary>
        /// Clear the cache, deleting all entries.
        /// </summary>
        void Clear();

        /// <summary>
        /// Looks up the cached value and version by the given key
        /// </summary>
        /// <param name="key">key for the lookup</param>
        /// <param name="result">value if the key is found, undefined otherwise</param>
        /// <param name="version">version of cached value if the key is found, undefined otherwise</param>
        /// <returns>true if the given key is in the cache</returns>
        bool LookUp(GrainId key, out IReadOnlyList<Tuple<SiloAddress, ActivationId>> result, out int version);

        /// <summary>
        /// Returns list of key-value-version tuples stored currently in the cache.
        /// </summary>
        IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> KeyValues { get; }
    }

    internal static class GrainDirectoryCacheExtensions
    {
        /// <summary>
        /// Looks up the cached value by the given key.
        /// </summary>
        /// <param name="cache">grain directory cache to look up results from</param>
        /// <param name="key">key for the lookup</param>
        /// <param name="result">value if the key is found, undefined otherwise</param>
        /// <returns>true if the given key is in the cache</returns>
        public static bool LookUp(this IGrainDirectoryCache cache, GrainId key, out IReadOnlyList<Tuple<SiloAddress, ActivationId>> result)
        {
            int version;
            return cache.LookUp(key, out result, out version);
        }
    }
}
