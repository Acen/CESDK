using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CESDK.Classes.Mono
{
    /// <summary>
    /// Search options for filtering results
    /// </summary>
    [Flags]
    public enum MonoSearchOptions
    {
        None = 0,
        Classes = 1,
        Methods = 2,
        Fields = 4,
        Modules = 8,
        All = Classes | Methods | Fields | Modules,
        CaseSensitive = 16,
        ExactMatch = 32
    }

    /// <summary>
    /// Progress information for long-running operations
    /// </summary>
    public class MonoIndexProgress
    {
        public int CurrentModule { get; set; }
        public int TotalModules { get; set; }
        public int CurrentTypeDef { get; set; }
        public int TotalTypeDefs { get; set; }
        public string CurrentItem { get; set; } = "";
        public double PercentComplete => TotalModules > 0 
            ? (CurrentModule * 100.0 / TotalModules) + (TotalTypeDefs > 0 ? (CurrentTypeDef * 100.0 / TotalTypeDefs / TotalModules) : 0)
            : 0;
    }

    /// <summary>
    /// High-performance, threaded search engine for .NET/Mono metadata.
    /// Builds an in-memory index for fast searching without blocking CE.
    /// </summary>
    public class MonoSearchEngine
    {
        private readonly MonoDataCollector _collector;
        private readonly object _indexLock = new();

        // Thread-safe search index
        private ConcurrentDictionary<string, List<MonoSearchResult>>? _searchIndex;
        private bool _indexBuilt;
        private bool _indexBuilding;

        // All cached data for fast access
        private List<MonoModule> _allModules = new();
        private List<MonoTypeDef> _allTypeDefs = new();
        private Dictionary<MonoTypeDef, List<MonoMethod>> _methodsByTypeDef = new();
        private Dictionary<MonoTypeDef, List<MonoField>> _fieldsByTypeDef = new();

        public MonoSearchEngine(MonoDataCollector collector)
        {
            _collector = collector;
        }

        /// <summary>
        /// Whether the search index has been built
        /// </summary>
        public bool IsIndexBuilt => _indexBuilt;

        /// <summary>
        /// Whether the index is currently being built
        /// </summary>
        public bool IsBuilding => _indexBuilding;

        /// <summary>
        /// Total number of indexed items
        /// </summary>
        public int IndexedItemCount => _searchIndex?.Values.Sum(v => v.Count) ?? 0;

        #region Index Building

        /// <summary>
        /// Builds the search index asynchronously in a background thread.
        /// This pre-loads all data so searches are fast and don't block CE.
        /// </summary>
        /// <param name="progress">Optional progress callback</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task BuildIndexAsync(
            IProgress<MonoIndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_indexBuilding)
                throw new InvalidOperationException("Index is already being built");

            if (!_collector.IsAttached)
                throw new MonoException("Not attached to a .NET/Mono process");

            _indexBuilding = true;

            try
            {
                await Task.Run(() => BuildIndexInternal(progress, cancellationToken), cancellationToken);
            }
            finally
            {
                _indexBuilding = false;
            }
        }

        private void BuildIndexInternal(IProgress<MonoIndexProgress>? progress, CancellationToken ct)
        {
            var newIndex = new ConcurrentDictionary<string, List<MonoSearchResult>>(StringComparer.OrdinalIgnoreCase);
            var allModules = new List<MonoModule>();
            var allTypeDefs = new List<MonoTypeDef>();
            var methodsByTypeDef = new Dictionary<MonoTypeDef, List<MonoMethod>>();
            var fieldsByTypeDef = new Dictionary<MonoTypeDef, List<MonoField>>();

            var progressReport = new MonoIndexProgress();

            // Step 1: Enumerate all domains and modules
            var domains = _collector.EnumDomains();
            ct.ThrowIfCancellationRequested();

            foreach (var domain in domains)
            {
                var modules = _collector.EnumModules(domain);
                allModules.AddRange(modules);
            }

            progressReport.TotalModules = allModules.Count;

            // Step 2: Enumerate all type definitions from all modules
            for (int i = 0; i < allModules.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var module = allModules[i];
                progressReport.CurrentModule = i + 1;
                progressReport.CurrentItem = module.Name;
                progress?.Report(progressReport);

                // Index the module itself
                AddToIndex(newIndex, module.Name, new MonoSearchResult
                {
                    Module = module,
                    ResultType = MonoSearchResultType.Module
                });

                try
                {
                    var typeDefs = _collector.EnumTypeDefs(module);
                    progressReport.TotalTypeDefs = typeDefs.Count;

                    for (int j = 0; j < typeDefs.Count; j++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var typeDef = typeDefs[j];
                        progressReport.CurrentTypeDef = j + 1;
                        progressReport.CurrentItem = typeDef.Name;
                        progress?.Report(progressReport);

                        allTypeDefs.Add(typeDef);

                        // Index the type definition (class)
                        AddToIndex(newIndex, typeDef.Name, new MonoSearchResult
                        {
                            Module = module,
                            TypeDef = typeDef,
                            ResultType = MonoSearchResultType.TypeDef
                        });

                        // Get and index methods
                        try
                        {
                            var methods = _collector.GetMethods(typeDef);
                            methodsByTypeDef[typeDef] = methods;

                            foreach (var method in methods)
                            {
                                AddToIndex(newIndex, method.Name, new MonoSearchResult
                                {
                                    Module = module,
                                    TypeDef = typeDef,
                                    Method = method,
                                    ResultType = MonoSearchResultType.Method
                                });
                            }
                        }
                        catch
                        {
                            // Skip methods that fail to load
                        }

                        // Get and index fields
                        try
                        {
                            var fields = _collector.GetFields(typeDef);
                            fieldsByTypeDef[typeDef] = fields;

                            foreach (var field in fields)
                            {
                                AddToIndex(newIndex, field.Name, new MonoSearchResult
                                {
                                    Module = module,
                                    TypeDef = typeDef,
                                    Field = field,
                                    ResultType = MonoSearchResultType.Field
                                });
                            }
                        }
                        catch
                        {
                            // Skip fields that fail to load
                        }
                    }
                }
                catch
                {
                    // Skip modules that fail to enumerate
                }
            }

            // Atomically update the index
            lock (_indexLock)
            {
                _searchIndex = newIndex;
                _allModules = allModules;
                _allTypeDefs = allTypeDefs;
                _methodsByTypeDef = methodsByTypeDef;
                _fieldsByTypeDef = fieldsByTypeDef;
                _indexBuilt = true;
            }
        }

        private void AddToIndex(ConcurrentDictionary<string, List<MonoSearchResult>> index, string key, MonoSearchResult result)
        {
            if (string.IsNullOrEmpty(key))
                return;

            // Add to full name
            index.AddOrUpdate(key,
                _ => new List<MonoSearchResult> { result },
                (_, list) => { list.Add(result); return list; });

            // Also index by individual words for partial matching
            var words = key.Split(new[] { '.', '_', '<', '>', '`', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word != key && word.Length > 2)
                {
                    index.AddOrUpdate(word,
                        _ => new List<MonoSearchResult> { result },
                        (_, list) => { list.Add(result); return list; });
                }
            }
        }

        #endregion

        #region Search Methods

        /// <summary>
        /// Searches the index for matching items. Very fast after index is built.
        /// </summary>
        /// <param name="query">Search query</param>
        /// <param name="options">Search options</param>
        /// <param name="maxResults">Maximum results to return (0 = unlimited)</param>
        public List<MonoSearchResult> Search(string query, MonoSearchOptions options = MonoSearchOptions.All, int maxResults = 100)
        {
            if (!_indexBuilt || _searchIndex == null)
                throw new InvalidOperationException("Search index not built. Call BuildIndexAsync first.");

            if (string.IsNullOrWhiteSpace(query))
                return new List<MonoSearchResult>();

            var results = new List<MonoSearchResult>();
            var seen = new HashSet<string>();
            bool caseSensitive = options.HasFlag(MonoSearchOptions.CaseSensitive);
            bool exactMatch = options.HasFlag(MonoSearchOptions.ExactMatch);

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            // If exact match, try direct lookup first
            if (exactMatch)
            {
                if (_searchIndex.TryGetValue(query, out var exactResults))
                {
                    foreach (var result in exactResults)
                    {
                        if (MatchesOptions(result, options) && AddUnique(results, result, seen))
                        {
                            if (maxResults > 0 && results.Count >= maxResults)
                                return results;
                        }
                    }
                }
                return results;
            }

            // Partial/contains search
            foreach (var kvp in _searchIndex)
            {
                bool matches = caseSensitive
                    ? kvp.Key.Contains(query)
                    : kvp.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                if (matches)
                {
                    foreach (var result in kvp.Value)
                    {
                        if (MatchesOptions(result, options) && AddUnique(results, result, seen))
                        {
                            if (maxResults > 0 && results.Count >= maxResults)
                                return results;
                        }
                    }
                }
            }

            // Sort results by relevance (exact matches first, then by name length)
            results.Sort((a, b) =>
            {
                var aName = GetResultName(a);
                var bName = GetResultName(b);

                // Exact matches first
                bool aExact = aName.Equals(query, comparison);
                bool bExact = bName.Equals(query, comparison);
                if (aExact != bExact) return aExact ? -1 : 1;

                // Starts with query next
                bool aStarts = aName.StartsWith(query, comparison);
                bool bStarts = bName.StartsWith(query, comparison);
                if (aStarts != bStarts) return aStarts ? -1 : 1;

                // Shorter names first
                return aName.Length.CompareTo(bName.Length);
            });

            return results;
        }

        /// <summary>
        /// Searches asynchronously without blocking
        /// </summary>
        public Task<List<MonoSearchResult>> SearchAsync(
            string query,
            MonoSearchOptions options = MonoSearchOptions.All,
            int maxResults = 100,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Search(query, options, maxResults), cancellationToken);
        }

        private bool MatchesOptions(MonoSearchResult result, MonoSearchOptions options)
        {
            return result.ResultType switch
            {
                MonoSearchResultType.Module => options.HasFlag(MonoSearchOptions.Modules),
                MonoSearchResultType.TypeDef => options.HasFlag(MonoSearchOptions.Classes),
                MonoSearchResultType.Method => options.HasFlag(MonoSearchOptions.Methods),
                MonoSearchResultType.Field => options.HasFlag(MonoSearchOptions.Fields),
                _ => true
            };
        }

        private bool AddUnique(List<MonoSearchResult> results, MonoSearchResult result, HashSet<string> seen)
        {
            var key = result.FullPath;
            if (seen.Contains(key))
                return false;

            seen.Add(key);
            results.Add(result);
            return true;
        }

        private string GetResultName(MonoSearchResult result)
        {
            return result.ResultType switch
            {
                MonoSearchResultType.Module => result.Module?.Name ?? "",
                MonoSearchResultType.TypeDef => result.TypeDef?.Name ?? "",
                MonoSearchResultType.Method => result.Method?.Name ?? "",
                MonoSearchResultType.Field => result.Field?.Name ?? "",
                _ => ""
            };
        }

        #endregion

        #region Quick Access Methods

        /// <summary>
        /// Gets all indexed modules
        /// </summary>
        public IReadOnlyList<MonoModule> AllModules => _allModules;

        /// <summary>
        /// Gets all indexed type definitions
        /// </summary>
        public IReadOnlyList<MonoTypeDef> AllTypeDefs => _allTypeDefs;

        /// <summary>
        /// Finds a class/type by exact name
        /// </summary>
        public MonoTypeDef? FindClass(string className)
        {
            var results = Search(className, MonoSearchOptions.Classes | MonoSearchOptions.ExactMatch, 1);
            return results.FirstOrDefault()?.TypeDef;
        }

        /// <summary>
        /// Finds a method by name (searches all classes)
        /// </summary>
        public List<MonoSearchResult> FindMethods(string methodName)
        {
            return Search(methodName, MonoSearchOptions.Methods);
        }

        /// <summary>
        /// Finds a field by name (searches all classes)
        /// </summary>
        public List<MonoSearchResult> FindFields(string fieldName)
        {
            return Search(fieldName, MonoSearchOptions.Fields);
        }

        /// <summary>
        /// Gets methods for a specific class (from cache)
        /// </summary>
        public List<MonoMethod> GetMethodsForClass(MonoTypeDef typeDef)
        {
            if (_methodsByTypeDef.TryGetValue(typeDef, out var methods))
                return methods;

            // Fall back to collector
            return _collector.GetMethods(typeDef);
        }

        /// <summary>
        /// Gets fields for a specific class (from cache)
        /// </summary>
        public List<MonoField> GetFieldsForClass(MonoTypeDef typeDef)
        {
            if (_fieldsByTypeDef.TryGetValue(typeDef, out var fields))
                return fields;

            // Fall back to collector
            return _collector.GetFields(typeDef);
        }

        #endregion

        #region Index Management

        /// <summary>
        /// Clears the search index and cached data
        /// </summary>
        public void ClearIndex()
        {
            lock (_indexLock)
            {
                _searchIndex?.Clear();
                _allModules.Clear();
                _allTypeDefs.Clear();
                _methodsByTypeDef.Clear();
                _fieldsByTypeDef.Clear();
                _indexBuilt = false;
            }

            // Also clear collector cache
            _collector.ClearCache();
        }

        #endregion
    }
}
