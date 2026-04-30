using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// Bundle returned by IUrlPrompt.ShowDownloadProgress. Closer pairs the dialog-close handle with the progress sink that drives the bar; both come from the same dialog and have identical lifetimes, so returning them together avoids an awkward `out` parameter at the call site.
public sealed record UrlProgressHandle(IDisposable Closer, IProgress<UrlDownloadProgress> Progress);

// User-interaction surface for the Open URL flow. Three responsibilities collapsed onto one interface so the VM has a single seam to mock for tests; production wires UrlPromptGtk for all three. Mirrors the IFilePicker pattern (one interface owning the dialog dance behind a Task-returning facade).
public interface IUrlPrompt
{
    // Returns the trimmed URL the user typed, or null if they dismissed the dialog. An empty string after trim is also returned as null so callers don't need to re-check.
    Task<string?> PromptForUrlAsync(string title);

    void ShowError(string title, string message);

    // Opens a modal progress window. The returned UrlProgressHandle bundles the close handle (caller disposes when the download completes / errors / is cancelled) and the IProgress sink (caller passes into IUrlDownloader.DownloadAsync). The CTS triggers when the user clicks Cancel; the VM observes the CTS to abort the download.
    UrlProgressHandle ShowDownloadProgress(string title, CancellationTokenSource cts);
}
