using System.Reflection;
using System.Runtime.Loader;
using MdfScope.Can;

namespace MdfScope;

/// <summary>Finds CAN adapter providers: built-in ones plus every plugin assembly under <c>plugins/</c>.</summary>
public static class PluginHost
{
    public static IReadOnlyList<ICanAdapterProvider> LoadProviders(out IReadOnlyList<string> problems)
    {
        var providers = new List<ICanAdapterProvider> { new DemoBusProvider() };
        var errors = new List<string>();
        string root = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(root))
        {
            foreach (var dll in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals("MdfScope.Can.dll", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var context = new PluginContext(dll);
                    var assembly = context.LoadFromAssemblyPath(dll);
                    foreach (var type in assembly.GetExportedTypes())
                    {
                        if (type.IsAbstract || !typeof(ICanAdapterProvider).IsAssignableFrom(type)) continue;
                        if (Activator.CreateInstance(type) is ICanAdapterProvider provider) providers.Add(provider);
                    }
                }
                catch (Exception e) when (e is BadImageFormatException or FileLoadException or ReflectionTypeLoadException
                                              or TargetInvocationException or MissingMethodException or TypeLoadException)
                {
                    errors.Add($"{Path.GetFileName(dll)}: {e.Message}");
                }
            }
        }
        problems = errors;
        return providers;
    }

    /// <summary>Isolates a plugin's own dependencies; the interface assembly is always the host's.</summary>
    sealed class PluginContext(string path) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path))
    {
        readonly AssemblyDependencyResolver resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(ICanAdapterProvider).Assembly.GetName().Name) return null;
            string? file = resolver.ResolveAssemblyToPath(name);
            return file != null ? LoadFromAssemblyPath(file) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? file = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return file != null ? LoadUnmanagedDllFromPath(file) : IntPtr.Zero;
        }
    }
}
