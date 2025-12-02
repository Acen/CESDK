using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CESDK.Lua;

namespace CESDK.Classes.Mono
{
    /// <summary>
    /// Exception thrown when Mono/DotNet operations fail
    /// </summary>
    public class MonoException : Exception
    {
        public MonoException(string message) : base(message) { }
        public MonoException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Represents a .NET/Mono domain
    /// </summary>
    public class MonoDomain
    {
        public IntPtr Handle { get; set; }
        public string Name { get; set; } = "";

        public override string ToString() => $"{Name} (0x{Handle.ToInt64():X})";
    }

    /// <summary>
    /// Represents a .NET/Mono module/assembly
    /// </summary>
    public class MonoModule
    {
        public IntPtr Handle { get; set; }
        public ulong BaseAddress { get; set; }
        public string Name { get; set; } = "";

        public override string ToString() => $"{Name} @ 0x{BaseAddress:X}";
    }

    /// <summary>
    /// Represents a .NET/Mono type definition (class/struct/enum)
    /// </summary>
    public class MonoTypeDef
    {
        public int Token { get; set; }
        public string Name { get; set; } = "";
        public int Flags { get; set; }
        public int Extends { get; set; }
        public IntPtr ModuleHandle { get; set; }

        // Cached data - lazily loaded
        internal List<MonoMethod>? _methods;
        internal List<MonoField>? _fields;
        internal MonoTypeData? _typeData;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Represents a .NET/Mono method
    /// </summary>
    public class MonoMethod
    {
        public int Token { get; set; }
        public string Name { get; set; } = "";
        public int Attributes { get; set; }
        public int ImplementationFlags { get; set; }
        public ulong ILCode { get; set; }
        public ulong NativeCode { get; set; }
        public List<ulong> SecondaryNativeCode { get; set; } = new();
        public List<MonoParameter> Parameters { get; set; } = new();

        public override string ToString() => $"{Name} @ 0x{NativeCode:X}";
    }

    /// <summary>
    /// Represents a method parameter
    /// </summary>
    public class MonoParameter
    {
        public string Name { get; set; } = "";
        public string CType { get; set; } = "";

        public override string ToString() => $"{CType} {Name}";
    }

    /// <summary>
    /// Represents a .NET/Mono field
    /// </summary>
    public class MonoField
    {
        public int Offset { get; set; }
        public string FieldType { get; set; } = "";
        public string Name { get; set; } = "";

        public override string ToString() => $"{FieldType} {Name} @ +0x{Offset:X}";
    }

    /// <summary>
    /// Represents type data including layout information
    /// </summary>
    public class MonoTypeData
    {
        public string ObjectType { get; set; } = "";
        public string ElementType { get; set; } = "";
        public int CountOffset { get; set; }
        public int ElementSize { get; set; }
        public int FirstElementOffset { get; set; }
        public string ClassName { get; set; } = "";
        public List<MonoField> Fields { get; set; } = new();
    }

    /// <summary>
    /// Represents a live .NET object instance
    /// </summary>
    public class MonoObjectInstance
    {
        public ulong StartAddress { get; set; }
        public int Size { get; set; }
        public int TypeToken1 { get; set; }
        public int TypeToken2 { get; set; }
        public string ClassName { get; set; } = "";

        public override string ToString() => $"{ClassName} @ 0x{StartAddress:X} ({Size} bytes)";
    }

    /// <summary>
    /// Search result containing matched items
    /// </summary>
    public class MonoSearchResult
    {
        public MonoModule? Module { get; set; }
        public MonoTypeDef? TypeDef { get; set; }
        public MonoMethod? Method { get; set; }
        public MonoField? Field { get; set; }
        public MonoSearchResultType ResultType { get; set; }

        public string FullPath
        {
            get
            {
                var parts = new List<string>();
                if (Module != null) parts.Add(Module.Name);
                if (TypeDef != null) parts.Add(TypeDef.Name);
                if (Method != null) parts.Add(Method.Name + "()");
                if (Field != null) parts.Add(Field.Name);
                return string.Join(".", parts);
            }
        }
    }

    public enum MonoSearchResultType
    {
        Module,
        TypeDef,
        Method,
        Field
    }

    /// <summary>
    /// High-performance .NET/Mono data collector with threading and caching support.
    /// Provides searchable access to decompiled .NET/Mono metadata.
    /// </summary>
    public class MonoDataCollector
    {
        private readonly LuaNative _lua;
        private readonly object _luaLock = new();

        // Cached database
        private List<MonoDomain>? _domains;
        private Dictionary<IntPtr, List<MonoModule>>? _modulesByDomain;
        private Dictionary<IntPtr, List<MonoTypeDef>>? _typeDefsByModule;
        private bool _isAttached;

        public MonoDataCollector()
        {
            _lua = PluginContext.Lua;
        }

        #region Attachment Status

        /// <summary>
        /// Checks if the data collector is attached to a .NET/Mono process
        /// </summary>
        public bool IsAttached
        {
            get
            {
                lock (_luaLock)
                {
                    try
                    {
                        _lua.GetGlobal("getDotNetDataCollector");
                        if (!_lua.IsFunction(-1))
                        {
                            _lua.Pop(1);
                            return false;
                        }

                        var result = _lua.PCall(0, 1);
                        if (result != 0)
                        {
                            _lua.Pop(1);
                            return false;
                        }

                        if (_lua.IsNil(-1))
                        {
                            _lua.Pop(1);
                            return false;
                        }

                        // Check the Attached property
                        _lua.GetField(-1, "Attached");
                        var attached = _lua.ToBoolean(-1);
                        _lua.Pop(2);

                        _isAttached = attached;
                        return attached;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        #endregion

        #region Domain Enumeration

        /// <summary>
        /// Enumerates all .NET/Mono domains in the target process
        /// </summary>
        public List<MonoDomain> EnumDomains()
        {
            if (_domains != null)
                return _domains;

            lock (_luaLock)
            {
                var domains = new List<MonoDomain>();

                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(1);
                        throw new MonoException("getDotNetDataCollector not available");
                    }

                    _lua.PCall(0, 1);
                    if (_lua.IsNil(-1))
                    {
                        _lua.Pop(1);
                        throw new MonoException("No DotNetDataCollector available");
                    }

                    // Call enumDomains method
                    _lua.GetField(-1, "enumDomains");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        throw new MonoException("enumDomains method not available");
                    }

                    _lua.PushValue(-2); // self
                    var result = _lua.PCall(1, 1);
                    if (result != 0)
                    {
                        var error = _lua.ToString(-1);
                        _lua.Pop(2);
                        throw new MonoException($"enumDomains failed: {error}");
                    }

                    // Parse the returned table
                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var domain = new MonoDomain();

                                _lua.GetField(-1, "DomainHandle");
                                if (_lua.IsInteger(-1))
                                    domain.Handle = new IntPtr(_lua.ToInteger(-1));
                                _lua.Pop(1);

                                _lua.GetField(-1, "Name");
                                domain.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                domains.Add(domain);
                            }
                            _lua.Pop(1);
                        }
                    }

                    _lua.Pop(2); // Pop table and collector
                    _domains = domains;
                    return domains;
                }
                catch (Exception ex) when (ex is not MonoException)
                {
                    throw new MonoException("Failed to enumerate domains", ex);
                }
            }
        }

        #endregion

        #region Module Enumeration

        /// <summary>
        /// Enumerates all modules in the specified domain
        /// </summary>
        public List<MonoModule> EnumModules(MonoDomain domain)
        {
            return EnumModules(domain.Handle);
        }

        /// <summary>
        /// Enumerates all modules in the specified domain
        /// </summary>
        public List<MonoModule> EnumModules(IntPtr domainHandle)
        {
            _modulesByDomain ??= new Dictionary<IntPtr, List<MonoModule>>();

            if (_modulesByDomain.TryGetValue(domainHandle, out var cached))
                return cached;

            lock (_luaLock)
            {
                var modules = new List<MonoModule>();

                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    _lua.PCall(0, 1);

                    _lua.GetField(-1, "enumModuleList");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        throw new MonoException("enumModuleList method not available");
                    }

                    _lua.PushValue(-2); // self
                    _lua.PushInteger(domainHandle.ToInt64());
                    var result = _lua.PCall(2, 1);
                    if (result != 0)
                    {
                        var error = _lua.ToString(-1);
                        _lua.Pop(2);
                        throw new MonoException($"enumModuleList failed: {error}");
                    }

                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var module = new MonoModule();

                                _lua.GetField(-1, "ModuleHandle");
                                if (_lua.IsInteger(-1))
                                    module.Handle = new IntPtr(_lua.ToInteger(-1));
                                _lua.Pop(1);

                                _lua.GetField(-1, "BaseAddress");
                                if (_lua.IsInteger(-1))
                                    module.BaseAddress = (ulong)_lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "Name");
                                module.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                modules.Add(module);
                            }
                            _lua.Pop(1);
                        }
                    }

                    _lua.Pop(2);
                    _modulesByDomain[domainHandle] = modules;
                    return modules;
                }
                catch (Exception ex) when (ex is not MonoException)
                {
                    throw new MonoException("Failed to enumerate modules", ex);
                }
            }
        }

        #endregion

        #region TypeDef Enumeration

        /// <summary>
        /// Enumerates all type definitions in the specified module
        /// </summary>
        public List<MonoTypeDef> EnumTypeDefs(MonoModule module)
        {
            return EnumTypeDefs(module.Handle);
        }

        /// <summary>
        /// Enumerates all type definitions in the specified module
        /// </summary>
        public List<MonoTypeDef> EnumTypeDefs(IntPtr moduleHandle)
        {
            _typeDefsByModule ??= new Dictionary<IntPtr, List<MonoTypeDef>>();

            if (_typeDefsByModule.TryGetValue(moduleHandle, out var cached))
                return cached;

            lock (_luaLock)
            {
                var typeDefs = new List<MonoTypeDef>();

                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    _lua.PCall(0, 1);

                    _lua.GetField(-1, "enumTypeDefs");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        throw new MonoException("enumTypeDefs method not available");
                    }

                    _lua.PushValue(-2); // self
                    _lua.PushInteger(moduleHandle.ToInt64());
                    var result = _lua.PCall(2, 1);
                    if (result != 0)
                    {
                        var error = _lua.ToString(-1);
                        _lua.Pop(2);
                        throw new MonoException($"enumTypeDefs failed: {error}");
                    }

                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var typeDef = new MonoTypeDef
                                {
                                    ModuleHandle = moduleHandle
                                };

                                _lua.GetField(-1, "TypeDefToken");
                                if (_lua.IsInteger(-1))
                                    typeDef.Token = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "Name");
                                typeDef.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                _lua.GetField(-1, "Flags");
                                if (_lua.IsInteger(-1))
                                    typeDef.Flags = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "Extends");
                                if (_lua.IsInteger(-1))
                                    typeDef.Extends = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                typeDefs.Add(typeDef);
                            }
                            _lua.Pop(1);
                        }
                    }

                    _lua.Pop(2);
                    _typeDefsByModule[moduleHandle] = typeDefs;
                    return typeDefs;
                }
                catch (Exception ex) when (ex is not MonoException)
                {
                    throw new MonoException("Failed to enumerate type definitions", ex);
                }
            }
        }

        #endregion

        #region Method Enumeration

        /// <summary>
        /// Gets all methods for a type definition
        /// </summary>
        public List<MonoMethod> GetMethods(MonoTypeDef typeDef)
        {
            if (typeDef._methods != null)
                return typeDef._methods;

            lock (_luaLock)
            {
                var methods = new List<MonoMethod>();

                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    _lua.PCall(0, 1);

                    _lua.GetField(-1, "getTypeDefMethods");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        throw new MonoException("getTypeDefMethods method not available");
                    }

                    _lua.PushValue(-2); // self
                    _lua.PushInteger(typeDef.ModuleHandle.ToInt64());
                    _lua.PushInteger(typeDef.Token);
                    var result = _lua.PCall(3, 1);
                    if (result != 0)
                    {
                        var error = _lua.ToString(-1);
                        _lua.Pop(2);
                        throw new MonoException($"getTypeDefMethods failed: {error}");
                    }

                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var method = new MonoMethod();

                                _lua.GetField(-1, "MethodToken");
                                if (_lua.IsInteger(-1))
                                    method.Token = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "Name");
                                method.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                _lua.GetField(-1, "Attributes");
                                if (_lua.IsInteger(-1))
                                    method.Attributes = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "ImplementationFlags");
                                if (_lua.IsInteger(-1))
                                    method.ImplementationFlags = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "ILCode");
                                if (_lua.IsInteger(-1))
                                    method.ILCode = (ulong)_lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "NativeCode");
                                if (_lua.IsInteger(-1))
                                    method.NativeCode = (ulong)_lua.ToInteger(-1);
                                _lua.Pop(1);

                                // Parse SecondaryNativeCode array
                                _lua.GetField(-1, "SecondaryNativeCode");
                                if (_lua.IsTable(-1))
                                {
                                    _lua.PushNil();
                                    while (_lua.Next(-2) != 0)
                                    {
                                        if (_lua.IsInteger(-1))
                                            method.SecondaryNativeCode.Add((ulong)_lua.ToInteger(-1));
                                        _lua.Pop(1);
                                    }
                                }
                                _lua.Pop(1);

                                methods.Add(method);
                            }
                            _lua.Pop(1);
                        }
                    }

                    _lua.Pop(2);
                    typeDef._methods = methods;
                    return methods;
                }
                catch (Exception ex) when (ex is not MonoException)
                {
                    throw new MonoException("Failed to get methods", ex);
                }
            }
        }

        /// <summary>
        /// Gets parameters for a method
        /// </summary>
        public List<MonoParameter> GetMethodParameters(IntPtr moduleHandle, int methodToken)
        {
            lock (_luaLock)
            {
                var parameters = new List<MonoParameter>();

                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    _lua.PCall(0, 1);

                    _lua.GetField(-1, "getMethodParameters");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        return parameters; // Not available
                    }

                    _lua.PushValue(-2);
                    _lua.PushInteger(moduleHandle.ToInt64());
                    _lua.PushInteger(methodToken);
                    var result = _lua.PCall(3, 1);
                    if (result != 0)
                    {
                        _lua.Pop(2);
                        return parameters;
                    }

                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var param = new MonoParameter();

                                _lua.GetField(-1, "Name");
                                param.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                _lua.GetField(-1, "CType");
                                param.CType = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                parameters.Add(param);
                            }
                            _lua.Pop(1);
                        }
                    }

                    _lua.Pop(2);
                    return parameters;
                }
                catch
                {
                    return parameters;
                }
            }
        }

        #endregion

        #region Field/Type Data

        /// <summary>
        /// Gets type data including fields for a type definition
        /// </summary>
        public MonoTypeData? GetTypeData(MonoTypeDef typeDef)
        {
            if (typeDef._typeData != null)
                return typeDef._typeData;

            lock (_luaLock)
            {
                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    _lua.PCall(0, 1);

                    _lua.GetField(-1, "getTypeDefData");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        return null;
                    }

                    _lua.PushValue(-2);
                    _lua.PushInteger(typeDef.ModuleHandle.ToInt64());
                    _lua.PushInteger(typeDef.Token);
                    var result = _lua.PCall(3, 1);
                    if (result != 0)
                    {
                        _lua.Pop(2);
                        return null;
                    }

                    if (!_lua.IsTable(-1))
                    {
                        _lua.Pop(2);
                        return null;
                    }

                    var typeData = new MonoTypeData();

                    _lua.GetField(-1, "ObjectType");
                    typeData.ObjectType = _lua.ToString(-1) ?? "";
                    _lua.Pop(1);

                    _lua.GetField(-1, "ElementType");
                    typeData.ElementType = _lua.ToString(-1) ?? "";
                    _lua.Pop(1);

                    _lua.GetField(-1, "CountOffset");
                    if (_lua.IsInteger(-1))
                        typeData.CountOffset = _lua.ToInteger(-1);
                    _lua.Pop(1);

                    _lua.GetField(-1, "ElementSize");
                    if (_lua.IsInteger(-1))
                        typeData.ElementSize = _lua.ToInteger(-1);
                    _lua.Pop(1);

                    _lua.GetField(-1, "FirstElementOffset");
                    if (_lua.IsInteger(-1))
                        typeData.FirstElementOffset = _lua.ToInteger(-1);
                    _lua.Pop(1);

                    _lua.GetField(-1, "ClassName");
                    typeData.ClassName = _lua.ToString(-1) ?? "";
                    _lua.Pop(1);

                    // Parse Fields array
                    _lua.GetField(-1, "Fields");
                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var field = new MonoField();

                                _lua.GetField(-1, "Offset");
                                if (_lua.IsInteger(-1))
                                    field.Offset = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "FieldType");
                                field.FieldType = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                _lua.GetField(-1, "Name");
                                field.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                typeData.Fields.Add(field);
                            }
                            _lua.Pop(1);
                        }
                    }
                    _lua.Pop(1);

                    _lua.Pop(2);

                    typeDef._typeData = typeData;
                    typeDef._fields = typeData.Fields;
                    return typeData;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Gets fields for a type definition
        /// </summary>
        public List<MonoField> GetFields(MonoTypeDef typeDef)
        {
            if (typeDef._fields != null)
                return typeDef._fields;

            var typeData = GetTypeData(typeDef);
            return typeData?.Fields ?? new List<MonoField>();
        }

        #endregion

        #region Address Data

        /// <summary>
        /// Gets type data for an object at a specific address
        /// </summary>
        public MonoTypeData? GetAddressData(ulong address)
        {
            lock (_luaLock)
            {
                try
                {
                    _lua.GetGlobal("getDotNetDataCollector");
                    _lua.PCall(0, 1);

                    _lua.GetField(-1, "getAddressData");
                    if (!_lua.IsFunction(-1))
                    {
                        _lua.Pop(2);
                        return null;
                    }

                    _lua.PushValue(-2);
                    _lua.PushInteger((long)address);
                    var result = _lua.PCall(2, 1);
                    if (result != 0)
                    {
                        _lua.Pop(2);
                        return null;
                    }

                    if (!_lua.IsTable(-1))
                    {
                        _lua.Pop(2);
                        return null;
                    }

                    var typeData = new MonoTypeData();

                    _lua.GetField(-1, "ClassName");
                    typeData.ClassName = _lua.ToString(-1) ?? "";
                    _lua.Pop(1);

                    _lua.GetField(-1, "ObjectType");
                    typeData.ObjectType = _lua.ToString(-1) ?? "";
                    _lua.Pop(1);

                    _lua.GetField(-1, "ElementType");
                    typeData.ElementType = _lua.ToString(-1) ?? "";
                    _lua.Pop(1);

                    _lua.GetField(-1, "FirstElementOffset");
                    if (_lua.IsInteger(-1))
                        typeData.FirstElementOffset = _lua.ToInteger(-1);
                    _lua.Pop(1);

                    // Parse Fields
                    _lua.GetField(-1, "Fields");
                    if (_lua.IsTable(-1))
                    {
                        _lua.PushNil();
                        while (_lua.Next(-2) != 0)
                        {
                            if (_lua.IsTable(-1))
                            {
                                var field = new MonoField();

                                _lua.GetField(-1, "Offset");
                                if (_lua.IsInteger(-1))
                                    field.Offset = _lua.ToInteger(-1);
                                _lua.Pop(1);

                                _lua.GetField(-1, "FieldType");
                                field.FieldType = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                _lua.GetField(-1, "Name");
                                field.Name = _lua.ToString(-1) ?? "";
                                _lua.Pop(1);

                                typeData.Fields.Add(field);
                            }
                            _lua.Pop(1);
                        }
                    }
                    _lua.Pop(1);

                    _lua.Pop(2);
                    return typeData;
                }
                catch
                {
                    return null;
                }
            }
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Clears all cached data
        /// </summary>
        public void ClearCache()
        {
            lock (_luaLock)
            {
                _domains = null;
                _modulesByDomain?.Clear();
                _typeDefsByModule?.Clear();
            }
        }

        /// <summary>
        /// Gets the Lua lock object for external synchronization
        /// </summary>
        internal object GetLuaLock() => _luaLock;

        #endregion
    }
}
