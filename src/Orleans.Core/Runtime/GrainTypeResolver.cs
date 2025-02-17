using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal interface IGrainTypeResolver
    {
        bool TryGetGrainClassData(Type grainInterfaceType, out GrainClassData implementation, string grainClassNamePrefix);
        bool TryGetGrainClassData(int grainInterfaceId, out GrainClassData implementation, string grainClassNamePrefix);
        bool TryGetGrainClassData(string grainImplementationClassName, out GrainClassData implementation);
        bool TryGetInterfaceData(int grainInterfaceId, out GrainInterfaceData interfaceData);
        bool IsUnordered(int grainTypeCode);
        string GetLoadedGrainAssemblies();
        string GetGrainTypeName(int typeCode);
    }

    [Serializable]
    internal class GrainTypeResolver : IGrainTypeResolver, IOnDeserialized
    {
        private readonly Dictionary<string, GrainInterfaceData> typeToInterfaceData;
        private readonly Dictionary<int, GrainInterfaceData> table;
        private readonly HashSet<string> loadedGrainAsemblies;
        private readonly HashSet<int> unordered;

        // Not serialized for backward compatibility, will be rebuilt from other available
        // data during deserialization
        [NonSerialized]
        private Dictionary<int, string> grainTypeToTypeName;

        public GrainTypeResolver(
            Dictionary<string, GrainInterfaceData> typeToInterfaceData,
            Dictionary<int, GrainInterfaceData> table,
            HashSet<string> loadedGrainAsemblies,
            HashSet<int> unordered)
        {
            this.typeToInterfaceData = typeToInterfaceData;
            this.table = table;
            this.loadedGrainAsemblies = loadedGrainAsemblies;
            this.unordered = unordered;
            BuildGrainTypeToTypeName();
        }

        public bool TryGetGrainClassData(Type interfaceType, out GrainClassData implementation, string grainClassNamePrefix)
        {
            implementation = null;
            GrainInterfaceData interfaceData;

            // First, try to find a non-generic grain implementation:
            if (this.typeToInterfaceData.TryGetValue(GrainInterfaceMap.GetTypeKey(interfaceType, false), out interfaceData) &&
                TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix))
            {
                return true;
            }

            // If a concrete implementation was not found and the interface is generic, 
            // try to find a generic grain implementation:
            if (interfaceType.IsGenericType &&
                this.typeToInterfaceData.TryGetValue(GrainInterfaceMap.GetTypeKey(interfaceType, true), out interfaceData) &&
                TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix))
            {
                return true;
            }

            return false;
        }

        public bool TryGetGrainClassData(int grainInterfaceId, out GrainClassData implementation, string grainClassNamePrefix = null)
        {
            implementation = null;
            GrainInterfaceData interfaceData;
            if (!table.TryGetValue(grainInterfaceId, out interfaceData))
            {
                return false;
            }
            return TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix);
        }

        public bool TryGetGrainClassData(string grainImplementationClassName, out GrainClassData implementation)
        {
            implementation = null;
            // have to iterate since _primaryImplementations is not serialized.
            foreach (var interfaceData in table.Values)
            {
                foreach (var implClass in interfaceData.Implementations)
                    if (implClass.GrainClass.Equals(grainImplementationClassName))
                    {
                        implementation = implClass;
                        return true;
                    }
            }
            return false;
        }

        public bool TryGetInterfaceData(int grainInterfaceId, out GrainInterfaceData interfaceData)
        {
            if (table.TryGetValue(grainInterfaceId, out interfaceData))
            {
                return true;
            }
            interfaceData = null;
            return false;
        }


        public string GetLoadedGrainAssemblies()
        {
            return loadedGrainAsemblies != null ? loadedGrainAsemblies.ToStrings() : String.Empty;
        }

        public bool IsUnordered(int grainTypeCode)
        {
            return unordered.Contains(grainTypeCode);
        }

        private static bool TryGetGrainClassData(GrainInterfaceData interfaceData, out GrainClassData implementation, string grainClassNamePrefix)
        {
            implementation = null;
            var implementations = interfaceData.Implementations;

            if (implementations.Length == 0)
                return false;

            if (String.IsNullOrEmpty(grainClassNamePrefix))
            {
                if (implementations.Length == 1)
                {
                    implementation = implementations[0];
                    return true;
                }

                if (interfaceData.PrimaryImplementation != null)
                {
                    implementation = interfaceData.PrimaryImplementation;
                    return true;
                }

                throw new OrleansException(String.Format("Cannot resolve grain interface ID={0} to a grain class because of multiple implementations of it: {1}",
                    interfaceData.InterfaceId, Utils.EnumerableToString(implementations, d => d.GrainClass, ",", false)));
            }

            if (implementations.Length == 1)
            {
                if (implementations[0].GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal))
                {
                    implementation = implementations[0];
                    return true;
                }

                return false;
            }

            var matches = implementations.Where(impl => impl.GrainClass.Equals(grainClassNamePrefix)).ToArray(); //exact match?
            if (matches.Length == 0)
                matches = implementations.Where(
                    impl => impl.GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal)).ToArray(); //prefix matches

            if (matches.Length == 0)
                return false;

            if (matches.Length == 1)
            {
                implementation = matches[0];
                return true;
            }

            throw new OrleansException(String.Format("Cannot resolve grain interface ID={0}, grainClassNamePrefix={1} to a grain class because of multiple implementations of it: {2}",
                interfaceData.InterfaceId,
                grainClassNamePrefix,
                Utils.EnumerableToString(matches, d => d.GrainClass, ",", false)));
        }

        public string GetGrainTypeName(int typeCode)
        {
            return this.grainTypeToTypeName.TryGetValue(typeCode, out var grainTypeName)
                ? grainTypeName
                : string.Empty;
        }

        public void OnDeserialized(ISerializerContext context) => BuildGrainTypeToTypeName();

        private void BuildGrainTypeToTypeName()
        {
            this.grainTypeToTypeName = new Dictionary<int, string>();
            foreach (var classData in this.table.Values.SelectMany(interfaceData => interfaceData.Implementations))
            {
                this.grainTypeToTypeName[classData.GrainTypeCode] = classData.GrainClass;
            }
        }
    }
}
