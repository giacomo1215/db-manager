#if DESKTOP
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace DbManager.Desktop;

// Serves the site's static assets (wwwroot, the scoped CSS bundle, collocated
// .razor.js modules) from assembly-embedded resources named "site/<route>".
// The desktop release is a single-file exe: AppContext.BaseDirectory points at
// a random extraction dir, so the on-disk static web assets manifest that
// MapStaticAssets requires can never be found there.
internal sealed class EmbeddedSiteFileProvider : IFileProvider
{
    private readonly Dictionary<string, EmbeddedFile> _files;

    public EmbeddedSiteFileProvider(Assembly assembly)
    {
        _files = new Dictionary<string, EmbeddedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("site/", StringComparison.OrdinalIgnoreCase))
                continue;
            var route = name["site/".Length..].Replace('\\', '/');
            _files['/' + route] = new EmbeddedFile(route, name, assembly);
        }
    }

    public IFileInfo GetFileInfo(string subpath) =>
        _files.TryGetValue(Normalize(subpath), out var file) ? file : new NotFoundFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string _) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string _) => NullChangeToken.Singleton;

    private static string Normalize(string subpath) => subpath.Replace('\\', '/').TrimEnd('/');

    private sealed class EmbeddedFile(string route, string resourceName, Assembly assembly) : IFileInfo
    {
        // StaticFiles middleware needs the size up front; stream it once at
        // startup (assets are small; the exe's cost is paid either way).
        public long Length { get; } = GetLength(resourceName, assembly);

        public string Name => route;
        public bool Exists => true;
        public bool IsDirectory => false;
        public string? PhysicalPath => null;
        // StaticFiles middleware converts this to a Win32 FILETIME, which
        // rejects DateTimeOffset.MinValue; any pre-1601 epoch works.
        public DateTimeOffset LastModified { get; } = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Stream CreateReadStream() =>
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' disappeared.");

        private static long GetLength(string resourceName, Assembly assembly)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
            return stream.Length;
        }
    }
}
#endif