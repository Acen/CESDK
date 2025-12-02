using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CESDK.Classes.Mono
{
    /// <summary>
    /// High-level facade for accessing .NET/Mono metadata in Cheat Engine.
    /// Provides a simple, thread-safe interface for searching and inspecting .NET types.
    /// 
    /// Usage:
    /// <code>
    /// var mono = new MonoAccess();
    /// 
    /// // Build the search index (do this once, can take a while for large assemblies)
    /// await mono.BuildIndexAsync(progress => Console.WriteLine($"Progress: {progress.PercentComplete}%"));
    /// 
    /// // Search for classes, methods, or fields
    /// var results = mono.Search("Player");
    /// foreach (var result in results)
    ///     Console.WriteLine(result.FullPath);
    /// 
    /// // Find all instances of a specific class
    /// var instances = await mono.FindInstancesAsync("GameManager");
    /// foreach (var obj in instances)
    ///     Console.WriteLine($"Found at {obj.Address:X}");
    /// </code>
    /// </summary>
    public class MonoAccess
    {
        private readonly MonoDataCollector _collector;
        private readonly MonoSearchEngine _search;
        private readonly MonoObjectEnumerator _objects;

        public MonoAccess()
        {
            _collector = new MonoDataCollector();
            _search = new MonoSearchEngine(_collector);
            _objects = new MonoObjectEnumerator(_collector);
        }

        #region Properties

        /// <summary>
        /// Whether attached to a .NET/Mono process
        /// </summary>
        public bool IsAttached => _collector.IsAttached;

        /// <summary>
        /// Whether the search index has been built
        /// </summary>
        public bool IsIndexReady => _search.IsIndexBuilt;

        /// <summary>
        /// Whether the index is currently being built
        /// </summary>
        public bool IsIndexing => _search.IsBuilding;

        /// <summary>
        /// Number of indexed items
        /// </summary>
        public int IndexedItemCount => _search.IndexedItemCount;

        /// <summary>
        /// Direct access to the data collector
        /// </summary>
        public MonoDataCollector Collector => _collector;

        /// <summary>
        /// Direct access to the search engine
        /// </summary>
        public MonoSearchEngine SearchEngine => _search;

        /// <summary>
        /// Direct access to the object enumerator
        /// </summary>
        public MonoObjectEnumerator Objects => _objects;

        #endregion

        #region Index Building

        /// <summary>
        /// Builds the search index asynchronously. Call this once before searching.
        /// </summary>
        /// <param name="progress">Optional progress callback</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task BuildIndexAsync(
            IProgress<MonoIndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await _search.BuildIndexAsync(progress, cancellationToken);
        }

        /// <summary>
        /// Builds the search index with a simple percentage callback
        /// </summary>
        public async Task BuildIndexAsync(Action<double> percentCallback, CancellationToken cancellationToken = default)
        {
            var progress = new Progress<MonoIndexProgress>(p => percentCallback(p.PercentComplete));
            await _search.BuildIndexAsync(progress, cancellationToken);
        }

        /// <summary>
        /// Clears the search index and all cached data
        /// </summary>
        public void ClearIndex()
        {
            _search.ClearIndex();
            _objects.ClearCache();
        }

        #endregion

        #region Searching

        /// <summary>
        /// Searches for classes, methods, and fields matching the query.
        /// Requires index to be built first.
        /// </summary>
        /// <param name="query">Search query (partial match)</param>
        /// <param name="maxResults">Maximum results (0 = unlimited)</param>
        /// <returns>List of matching results</returns>
        public List<MonoSearchResult> Search(string query, int maxResults = 100)
        {
            return _search.Search(query, MonoSearchOptions.All, maxResults);
        }

        /// <summary>
        /// Searches for classes matching the query
        /// </summary>
        public List<MonoSearchResult> SearchClasses(string query, int maxResults = 100)
        {
            return _search.Search(query, MonoSearchOptions.Classes, maxResults);
        }

        /// <summary>
        /// Searches for methods matching the query
        /// </summary>
        public List<MonoSearchResult> SearchMethods(string query, int maxResults = 100)
        {
            return _search.Search(query, MonoSearchOptions.Methods, maxResults);
        }

        /// <summary>
        /// Searches for fields matching the query
        /// </summary>
        public List<MonoSearchResult> SearchFields(string query, int maxResults = 100)
        {
            return _search.Search(query, MonoSearchOptions.Fields, maxResults);
        }

        /// <summary>
        /// Finds a class by exact name
        /// </summary>
        public MonoTypeDef? FindClass(string className)
        {
            return _search.FindClass(className);
        }

        /// <summary>
        /// Searches asynchronously
        /// </summary>
        public async Task<List<MonoSearchResult>> SearchAsync(
            string query,
            MonoSearchOptions options = MonoSearchOptions.All,
            int maxResults = 100,
            CancellationToken cancellationToken = default)
        {
            return await _search.SearchAsync(query, options, maxResults, cancellationToken);
        }

        #endregion

        #region Object Enumeration

        /// <summary>
        /// Finds all live instances of a class by name
        /// </summary>
        /// <param name="typeName">Full or partial type name</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of object instances in memory</returns>
        public async Task<List<MonoObjectInfo>> FindInstancesAsync(
            string typeName,
            CancellationToken cancellationToken = default)
        {
            return await _objects.EnumObjectsOfTypeAsync(typeName, cancellationToken);
        }

        /// <summary>
        /// Finds all live instances of a specific type
        /// </summary>
        public async Task<List<MonoObjectInfo>> FindInstancesAsync(
            MonoTypeDef typeDef,
            CancellationToken cancellationToken = default)
        {
            return await _objects.EnumObjectsOfTypeAsync(typeDef, cancellationToken);
        }

        /// <summary>
        /// Enumerates ALL objects in the process.
        /// WARNING: This can be very slow and return millions of objects!
        /// </summary>
        public async Task<List<MonoObjectInfo>> EnumAllObjectsAsync(
            IProgress<MonoObjectEnumProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await _objects.EnumAllObjectsAsync(progress, cancellationToken);
        }

        /// <summary>
        /// Gets type information for an object at a specific address
        /// </summary>
        public MonoTypeData? GetObjectInfo(ulong address)
        {
            return _collector.GetAddressData(address);
        }

        #endregion

        #region Domain/Module Access

        /// <summary>
        /// Gets all .NET domains in the target process
        /// </summary>
        public List<MonoDomain> GetDomains()
        {
            return _collector.EnumDomains();
        }

        /// <summary>
        /// Gets all modules in a domain
        /// </summary>
        public List<MonoModule> GetModules(MonoDomain domain)
        {
            return _collector.EnumModules(domain);
        }

        /// <summary>
        /// Gets all type definitions in a module
        /// </summary>
        public List<MonoTypeDef> GetTypeDefs(MonoModule module)
        {
            return _collector.EnumTypeDefs(module);
        }

        /// <summary>
        /// Gets all methods in a type
        /// </summary>
        public List<MonoMethod> GetMethods(MonoTypeDef typeDef)
        {
            return _collector.GetMethods(typeDef);
        }

        /// <summary>
        /// Gets all fields in a type
        /// </summary>
        public List<MonoField> GetFields(MonoTypeDef typeDef)
        {
            return _collector.GetFields(typeDef);
        }

        /// <summary>
        /// Gets all modules from all domains (cached from index if available)
        /// </summary>
        public IReadOnlyList<MonoModule> AllModules => _search.AllModules;

        /// <summary>
        /// Gets all type definitions from all modules (cached from index if available)
        /// </summary>
        public IReadOnlyList<MonoTypeDef> AllTypeDefs => _search.AllTypeDefs;

        #endregion
    }
}
