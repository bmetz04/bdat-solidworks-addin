using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>
    /// Talks to the "3DEXPERIENCE PLM Services" connector add-in that ships with 3DEXPERIENCE SOLIDWORKS.
    ///
    /// The official SolidWorks API can't pick a bookmark when saving, but the connector's own API can.
    /// The connector keeps its API object in the public static field TaskPaneInfra.EnoSwInterfaceImpl._swInterface
    /// (Dassault's own Batch Save add-in reads it the same way), and hands out managers by name through
    /// IEnoSwInterface.GetManager: "Save", "Authoring", "FileCache", "BookmarkChooser", "Connection", ...
    ///
    /// Everything goes through reflection so BDAT still builds and loads on PCs without the connector.
    /// </summary>
    internal sealed class Connector
    {
        private const string InfraAssembly = "TaskPaneInfra";
        private const string ItfAssembly = "EnoSwApiItf";
        private const string PlmServicesClsid = "{DD2533E5-1513-40D8-82B4-927790D0A896}";
        private const string DefaultFolder = @"C:\Program Files\Dassault Systemes\B428xcadconnectors\win_b64\USWC";

        private readonly object _api;
        private readonly Assembly _itf;

        private Connector(object api, Assembly itf)
        {
            _api = api;
            _itf = itf;
        }

        /// <summary>The running connector, or null if it isn't loaded in this SolidWorks session.</summary>
        public static Connector Find()
        {
            TestMode.BlockConnector("Connector.Find");
            Assembly infra = Loaded(InfraAssembly);
            Assembly itf = Loaded(ItfAssembly);
            if (infra == null || itf == null)
            {
                string folder = ConnectorFolder();
                if (folder == null) return null;
                if (infra == null) infra = Assembly.LoadFrom(Path.Combine(folder, InfraAssembly + ".dll"));
                if (itf == null) itf = Assembly.LoadFrom(Path.Combine(folder, ItfAssembly + ".dll"));
            }

            Type impl = infra.GetType("TaskPaneInfra.EnoSwInterfaceImpl");
            FieldInfo field = impl == null ? null : impl.GetField("_swInterface", BindingFlags.Public | BindingFlags.Static);
            object api = field == null ? null : field.GetValue(null);
            return api == null ? null : new Connector(api, itf);
        }

        public bool IsConnected
        {
            get
            {
                try
                {
                    object status = Call(Manager("Connection"), "IEnoSwConnection2", "GetConnectionStatus");
                    return status != null && status.ToString() == "connected";
                }
                catch
                {
                    return false;
                }
            }
        }

        public object Manager(string capability)
        {
            return Call(_api, "IEnoSwInterface", "GetManager", capability);
        }

        /// <summary>Calls a method declared on the named EnoSwApiItf interface.</summary>
        public object Call(object target, string interfaceName, string method, params object[] args)
        {
            TestMode.BlockConnector(interfaceName + "." + method);
            MethodInfo m = Interface(interfaceName).GetMethod(method);
            if (m == null) throw new MissingMethodException(interfaceName, method);
            try
            {
                return m.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        public object Get(object target, string interfaceName, string property)
        {
            TestMode.BlockConnector(interfaceName + "." + property);
            return Property(interfaceName, property).GetValue(target, null);
        }

        public void Set(object target, string interfaceName, string property, object value)
        {
            TestMode.BlockConnector(interfaceName + "." + property);
            Property(interfaceName, property).SetValue(target, value, null);
        }

        private PropertyInfo Property(string interfaceName, string property)
        {
            PropertyInfo p = Interface(interfaceName).GetProperty(property);
            if (p == null) throw new MissingMemberException(interfaceName, property);
            return p;
        }

        private Type Interface(string name)
        {
            Type t = _itf.GetType(ItfAssembly + "." + name);
            if (t == null) throw new TypeLoadException("The 3DEXPERIENCE connector has no " + name + ". It may be a different version.");
            return t;
        }

        private static Assembly Loaded(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == name) return a;
            }
            return null;
        }

        private static string ConnectorFolder()
        {
            using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + PlmServicesClsid + @"\InprocServer32"))
            {
                string dll = key == null ? null : key.GetValue(null) as string;
                if (!string.IsNullOrEmpty(dll) && File.Exists(dll)) return Path.GetDirectoryName(dll);
            }
            return Directory.Exists(DefaultFolder) ? DefaultFolder : null;
        }
    }
}
