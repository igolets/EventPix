namespace EventPix.Web.Media;

/// <summary>
/// Optional malware hook, called with every upload's content before anything is stored.
/// EventPix ships with a no-op default; operators who want scanning implement this interface
/// (e.g. against a ClamAV daemon), register it as IAntivirusScanner in DI and let it throw on
/// rejection. Fail-closed: any exception is reported to the uploader as rejected_by_scanner
/// and nothing is persisted.
/// </summary>
internal interface IAntivirusScanner
{
    /// <summary>Throws when the content must not be stored. The stream is at position 0.</summary>
    public Task ScanAsync(Stream content, string fileName, CancellationToken ct);
}

internal sealed class NoAntivirusScanner : IAntivirusScanner
{
    public Task ScanAsync(Stream content, string fileName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
