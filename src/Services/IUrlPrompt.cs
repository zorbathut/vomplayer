using System;
using System.Threading.Tasks;

namespace Vomplayer.Services;

// A live URL-load status shown in the video window. Created by IUrlPrompt.ShowUrlStatus and disposed by the caller when its phase ends (probe done, classify+download done, cancelled, errored). Progress feeds download ticks into the overlay's bar; Dispose hides the overlay. The handle is a generation token: the shared overlay only acts on the *current* handle, so one VideoContext's load finishing can't tear down another's still-live status (Primary + Secondary PiP share a single overlay).
public interface IUrlStatusHandle : IDisposable
{
    IProgress<UrlDownloadProgress> Progress { get; }
}

// User-interaction surface for the Open URL flow. Three responsibilities collapsed onto one interface so the VM has a single seam to mock for tests; production wires UrlPromptGtk for all three. Mirrors the IFilePicker pattern (one interface owning the dialog dance behind a Task-returning facade).
public interface IUrlPrompt
{
    // Returns the trimmed URL the user typed, or null if they dismissed the dialog. An empty string after trim is also returned as null so callers don't need to re-check.
    Task<string?> PromptForUrlAsync(string title);

    void ShowError(string title, string message);

    // Shows the in-window status overlay (composited over the video) with the given text, immediately and non-modally. onCancel, when non-null, exposes a Cancel affordance wired to it (probe/classify/download can all be cancellable); pass null for a non-cancellable busy state. The returned handle's Progress drives the download bar; dispose it to hide the overlay.
    IUrlStatusHandle ShowUrlStatus(string statusText, Action? onCancel);
}
