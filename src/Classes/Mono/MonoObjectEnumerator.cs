using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CESDK.Lua;

namespace CESDK.Classes.Mono
{
    /// <summary>
    /// Represents a live .NET/Mono object instance in memory
    /// </summary>
    public class MonoObjectInfo
    {
        /// <summary>
        /// Memory address of the object instance
        /// </summary>
        public ulong Address { get; set; }

        /// <summary>
        /// The type (class) of this object
        /// </summary>
        public MonoTypeDef? TypeDef { get; set; }

        /// <summary>
        /// Domain the object belongs to
        /// </summary>
        public MonoDomain? Domain { get; set; }

        /// <summary>
        /// Type name
        /// </summary>
        public string TypeName { get; set; } = "";

        /// <summary>
        /// Full namespace.classname
        /// </summary>
        public string FullTypeName { get; set; } = "";

        public override string ToString()
        {
            return $"[{Address:X}] {FullTypeName}";
        }
    }

    /// <summary>
    /// Progress for object enumeration
    /// </summary>
    public class MonoObjectEnumProgress
    {
        public int ObjectsFound { get; set; }
        public string CurrentType { get; set; } = "";
        public bool IsScanning { get; set; }
    }

    /// <summary>
    /// Enumerates live .NET/Mono object instances in the target process.
    /// Uses CE's enumAllObjects / enumAllObjectsOfType functions.
    /// </summary>
    public class MonoObjectEnumerator
    {
        private readonly LuaNative _lua;
        private readonly object _luaLock;
        private readonly MonoDataCollector _collector;

        // Cache of found objects
        private ConcurrentDictionary<ulong, MonoObjectInfo> _objects = new();
        private bool _isEnumerating;

        public MonoObjectEnumerator(MonoDataCollector collector)
        {
            _collector = collector;
            _lua = PluginContext.Lua;
            _luaLock = collector.GetLuaLock();
        }

        /// <summary>
        /// Whether an enumeration is currently in progress
        /// </summary>
        public bool IsEnumerating => _isEnumerating;

        /// <summary>
        /// Number of cached objects
        /// </summary>
        public int CachedObjectCount => _objects.Count;

        /// <summary>
        /// Gets all cached objects
        /// </summary>
        public IReadOnlyDictionary<ulong, MonoObjectInfo> CachedObjects => _objects;

        #region Enumerate All Objects

        /// <summary>
        /// Enumerates all .NET/Mono object instances in the target process.
        /// WARNING: This can be very slow and return millions of objects!
        /// </summary>
        /// <param name="progress">Progress callback</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of all object instances</returns>
        public async Task<List<MonoObjectInfo>> EnumAllObjectsAsync(
            IProgress<MonoObjectEnumProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_isEnumerating)
                throw new InvalidOperationException("Object enumeration already in progress");

            if (!_collector.IsAttached)
                throw new MonoException("Not attached to a .NET/Mono process");

            _isEnumerating = true;

            try
            {
                return await Task.Run(() => EnumAllObjectsInternal(progress, cancellationToken), cancellationToken);
            }
            finally
            {
                _isEnumerating = false;
            }
        }

        private List<MonoObjectInfo> EnumAllObjectsInternal(
            IProgress<MonoObjectEnumProgress>? progress,
            CancellationToken ct)
        {
            var results = new List<MonoObjectInfo>();
            var progressReport = new MonoObjectEnumProgress { IsScanning = true };

            lock (_luaLock)
            {
                // Get DotNetDataCollector
                _lua.GetGlobal("DotNetDataCollector");
                if (!_lua.IsTable(-1))
                {
                    _lua.Pop(1);
                    throw new MonoException("DotNetDataCollector not available");
                }

                // Call enumAllObjects()
                _lua.GetField(-1, "enumAllObjects");
                if (!_lua.IsFunction(-1))
                {
                    _lua.Pop(2);
                    throw new MonoException("enumAllObjects not available");
                }

                _lua.PushValue(-2); // Push DotNetDataCollector as self
                int callResult = _lua.PCall(1, 1);

                if (callResult != 0)
                {
                    string error = _lua.ToString(-1) ?? "Unknown error";
                    _lua.Pop(2);
                    throw new MonoException($"enumAllObjects failed: {error}");
                }

                // Result should be a table of objects
                if (_lua.IsTable(-1))
                {
                    int tableIndex = _lua.GetTop();
                    int objIndex = 0;

                    _lua.PushNil();
                    while (_lua.Next(tableIndex) != 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        var obj = ParseObjectInfo();
                        if (obj != null)
                        {
                            results.Add(obj);
                            _objects.TryAdd(obj.Address, obj);

                            if (++objIndex % 1000 == 0)
                            {
                                progressReport.ObjectsFound = objIndex;
                                progress?.Report(progressReport);
                            }
                        }

                        _lua.Pop(1); // Pop value, keep key for next iteration
                    }
                }

                _lua.Pop(2); // Pop result and DotNetDataCollector
            }

            progressReport.ObjectsFound = results.Count;
            progressReport.IsScanning = false;
            progress?.Report(progressReport);

            return results;
        }

        #endregion

        #region Enumerate Objects of Specific Type

        /// <summary>
        /// Enumerates all instances of a specific type.
        /// Much faster than enumerating all objects.
        /// </summary>
        /// <param name="typeDef">The type to find instances of</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of object instances</returns>
        public async Task<List<MonoObjectInfo>> EnumObjectsOfTypeAsync(
            MonoTypeDef typeDef,
            CancellationToken cancellationToken = default)
        {
            if (!_collector.IsAttached)
                throw new MonoException("Not attached to a .NET/Mono process");

            return await Task.Run(() => EnumObjectsOfTypeInternal(typeDef, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Enumerates all instances of a type by name.
        /// </summary>
        /// <param name="typeName">Full or partial type name</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<List<MonoObjectInfo>> EnumObjectsOfTypeAsync(
            string typeName,
            CancellationToken cancellationToken = default)
        {
            // First find the type definition
            var results = new List<MonoObjectInfo>();
            var domains = _collector.EnumDomains();

            foreach (var domain in domains)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modules = _collector.EnumModules(domain);
                foreach (var module in modules)
                {
                    var typeDefs = _collector.EnumTypeDefs(module);
                    var matchingType = typeDefs.Find(t =>
                        t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                        t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (matchingType != null)
                    {
                        var instances = await EnumObjectsOfTypeAsync(matchingType, cancellationToken);
                        results.AddRange(instances);
                    }
                }
            }

            return results;
        }

        private List<MonoObjectInfo> EnumObjectsOfTypeInternal(MonoTypeDef typeDef, CancellationToken ct)
        {
            var results = new List<MonoObjectInfo>();

            lock (_luaLock)
            {
                // Get DotNetDataCollector
                _lua.GetGlobal("DotNetDataCollector");
                if (!_lua.IsTable(-1))
                {
                    _lua.Pop(1);
                    throw new MonoException("DotNetDataCollector not available");
                }

                // Call enumAllObjectsOfType(typeDefToken)
                _lua.GetField(-1, "enumAllObjectsOfType");
                if (!_lua.IsFunction(-1))
                {
                    _lua.Pop(2);
                    throw new MonoException("enumAllObjectsOfType not available");
                }

                _lua.PushValue(-2); // Push DotNetDataCollector as self
                _lua.PushInteger(typeDef.Token);

                int callResult = _lua.PCall(2, 1);

                if (callResult != 0)
                {
                    string error = _lua.ToString(-1) ?? "Unknown error";
                    _lua.Pop(2);
                    throw new MonoException($"enumAllObjectsOfType failed: {error}");
                }

                // Parse results
                if (_lua.IsTable(-1))
                {
                    int tableIndex = _lua.GetTop();

                    _lua.PushNil();
                    while (_lua.Next(tableIndex) != 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        var obj = ParseObjectInfo();
                        if (obj != null)
                        {
                            obj.TypeDef = typeDef;
                            obj.TypeName = typeDef.Name;
                            obj.FullTypeName = typeDef.Name;
                            results.Add(obj);
                            _objects.TryAdd(obj.Address, obj);
                        }

                        _lua.Pop(1);
                    }
                }

                _lua.Pop(2);
            }

            return results;
        }

        #endregion

        #region Object Inspection

        /// <summary>
        /// Reads a field value from an object instance
        /// </summary>
        /// <typeparam name="T">Expected value type</typeparam>
        /// <param name="obj">The object instance</param>
        /// <param name="field">The field to read</param>
        /// <returns>Field value or default</returns>
        public T? ReadField<T>(MonoObjectInfo obj, MonoField field)
        {
            if (obj.TypeDef == null)
                throw new ArgumentException("Object has no type information");

            // Use getAddressData to get field value
            var addressData = _collector.GetAddressData(obj.Address);
            if (addressData == null)
                return default;

            // For now, return the base value - full field reading would need more implementation
            return default;
        }

        /// <summary>
        /// Gets address data for an object
        /// </summary>
        public MonoTypeData? GetObjectInfo(ulong address)
        {
            return _collector.GetAddressData(address);
        }

        #endregion

        #region Helpers

        private MonoObjectInfo? ParseObjectInfo()
        {
            if (!_lua.IsTable(-1))
                return null;

            var obj = new MonoObjectInfo();

            // Get address
            _lua.GetField(-1, "address");
            if (_lua.IsNumber(-1))
                obj.Address = (ulong)_lua.ToNumber(-1);
            else if (_lua.IsString(-1) && ulong.TryParse(_lua.ToString(-1), out ulong addr))
                obj.Address = addr;
            _lua.Pop(1);

            // Get type name
            _lua.GetField(-1, "typename");
            if (_lua.IsString(-1))
                obj.TypeName = _lua.ToString(-1) ?? "";
            _lua.Pop(1);

            // Get full type name
            _lua.GetField(-1, "classname");
            if (_lua.IsString(-1))
                obj.FullTypeName = _lua.ToString(-1) ?? "";
            _lua.Pop(1);

            if (string.IsNullOrEmpty(obj.FullTypeName))
                obj.FullTypeName = obj.TypeName;

            return obj.Address != 0 ? obj : null;
        }

        /// <summary>
        /// Clears the object cache
        /// </summary>
        public void ClearCache()
        {
            _objects.Clear();
        }

        #endregion
    }
}
