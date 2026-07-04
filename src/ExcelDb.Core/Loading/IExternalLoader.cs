using System.Threading.Tasks;

namespace ExcelDb.Loading
{
    /// <summary>
    /// Host-provided resolver for one external resource scheme ("unity", "fmod"...).
    /// Stateless pure IO by contract: caching, refcounting and in-flight dedupe
    /// live in the framework's resource manager.
    /// </summary>
    public interface IExternalLoader
    {
        string Scheme { get; }

        ValueTask<object?> LoadAsync(ExternalKey key);
        void Unload(ExternalKey key, object resource);
    }
}
