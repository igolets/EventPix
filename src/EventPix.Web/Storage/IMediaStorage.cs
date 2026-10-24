using EventPix.Web.Configuration;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Storage;

/// <summary>Blob storage abstraction. Keys are server-generated relative paths like "{event}/{media}/original".</summary>
internal interface IMediaStorage
{
    public Task SaveAsync(string key, Stream content, CancellationToken ct);
    public Stream OpenRead(string key);
    public bool Exists(string key);
    public Task DeleteAsync(string key, CancellationToken ct);
}

internal sealed class FileSystemMediaStorage : IMediaStorage
{
    private readonly string _root;

    public FileSystemMediaStorage(IOptions<EventPixOptions> options)
    {
        _root = Path.GetFullPath(options.Value.StoragePath);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Maps a key to a file path and guarantees the result stays inside the storage root.</summary>
    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));
        var rel = Path.GetRelativePath(_root, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            throw new ArgumentException("Storage key escapes the root.", nameof(key));
        }

        return full;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await content.CopyToAsync(fs, ct);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    public Stream OpenRead(string key)
    {
        return new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    public bool Exists(string key)
    {
        return File.Exists(Resolve(key));
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
