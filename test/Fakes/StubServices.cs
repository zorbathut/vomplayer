using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vomplayer.Services;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

// Shared fakes for the service interfaces VideoContext / ViewModelMain consume. One class per service, superset of the tracking members the per-suite copies carried. Every fake's default configuration behaves like the inert stub it replaced (pickers return null, no stored positions/preferences), so suites that only need a placeholder construct them bare; suites that assert on interactions read the tracking members. Exception: FakeUrlDownloader defaults to Available=true / YtDlpDownload (the URL-routing tests' common case) — construct with { Available = false } for the inert-stub behavior of "yt-dlp not installed".

internal sealed class FakeFilePicker : IFilePicker
{
    public string? NextResult { get; set; }
    public string? NextAudioResult { get; set; }
    public string? NextSubtitleResult { get; set; }
    public int Calls { get; private set; }
    public int AudioCalls { get; private set; }
    public int SubtitleCalls { get; private set; }

    public Task<string?> PickVideoFileAsync(string title)
    {
        Calls++;
        return Task.FromResult(NextResult);
    }

    public Task<string?> PickAudioFileAsync(string title)
    {
        AudioCalls++;
        return Task.FromResult(NextAudioResult);
    }

    public Task<string?> PickSubtitleFileAsync(string title)
    {
        SubtitleCalls++;
        return Task.FromResult(NextSubtitleResult);
    }
}

internal sealed class FakeRecentFiles : IRecentFiles
{
    public List<string> RecordedPaths { get; } = new();
    // Pre-seed via the dictionary to simulate "this file already has a saved position"; mutated by RecordPosition for save-side assertions.
    public Dictionary<string, double> Positions { get; } = new();
    public List<(string Path, double Position)> RecordedPositions { get; } = new();
    public List<string> GetPositionCalls { get; } = new();

    public void Record(string pathOrUri)
    {
        RecordedPaths.Add(pathOrUri);
    }

    public void RecordPosition(string pathOrUri, double positionSeconds)
    {
        RecordedPositions.Add((pathOrUri, positionSeconds));
        Positions[pathOrUri] = positionSeconds;
    }

    public double? GetPosition(string pathOrUri)
    {
        GetPositionCalls.Add(pathOrUri);
        return Positions.TryGetValue(pathOrUri, out var p) ? p : null;
    }
}

internal sealed class FakeTrackPreferences : ITrackPreferences
{
    // Order-preserving log of (directory, kind, preference) to make assertion-by-equality easy in save tests.
    public List<(string Dir, MediaKind Kind, TrackPreference Pref)> RecordedPrefs { get; } = new();
    // Pre-seeded responses for Get; tests put a value here to simulate "this directory has a saved preference for this kind".
    public Dictionary<(string Dir, MediaKind Kind), TrackPreference> Stored { get; } = new();
    public List<(string Dir, MediaKind Kind)> GetCalls { get; } = new();

    public void Record(string directory, MediaKind kind, TrackPreference preference)
    {
        RecordedPrefs.Add((directory, kind, preference));
        Stored[(directory, kind)] = preference;
    }

    public TrackPreference? Get(string directory, MediaKind kind)
    {
        GetCalls.Add((directory, kind));
        return Stored.TryGetValue((directory, kind), out var p) ? p : null;
    }
}

internal sealed class FakeUrlDownloader : IUrlDownloader
{
    public bool Available { get; set; } = true;
    public IReadOnlyList<string> NextProbeResult { get; set; } = Array.Empty<string>();
    // Default to YtDlpDownload so legacy tests written before the probe step still exercise the download path. Tests that want to validate the mpv-direct branch set this to MpvDirect explicitly.
    public UrlLoadKind ClassifyResult { get; set; } = UrlLoadKind.YtDlpDownload;
    public Exception? ClassifyException { get; set; }
    public Func<string, string>? DownloadResolver { get; set; }
    public TaskCompletionSource<string>? PendingDownload { get; set; }
    public List<string> ProbeCalls { get; } = new();
    public List<string> ClassifyCalls { get; } = new();
    public List<string> DownloadCalls { get; } = new();
    public List<CancellationToken> ObservedTokens { get; } = new();

    public Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        return Task.FromResult(Available);
    }

    public Task<IReadOnlyList<string>> ProbeAsync(string url, CancellationToken ct)
    {
        ProbeCalls.Add(url);
        return Task.FromResult(NextProbeResult);
    }

    public Task<UrlLoadKind> ClassifyAsync(string url, CancellationToken ct)
    {
        ClassifyCalls.Add(url);
        if (ClassifyException != null)
        {
            throw ClassifyException;
        }
        return Task.FromResult(ClassifyResult);
    }

    public Task<string> DownloadAsync(string url, IProgress<UrlDownloadProgress>? progress, CancellationToken ct)
    {
        DownloadCalls.Add(url);
        ObservedTokens.Add(ct);
        if (PendingDownload != null)
        {
            // Capture the TCS at call time so a later test mutation of PendingDownload (e.g., setting it to null before triggering the next download) doesn't NRE the cancellation callback.
            var tcs = PendingDownload;
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
        if (DownloadResolver != null)
        {
            return Task.FromResult(DownloadResolver(url));
        }
        return Task.FromResult($"/fake/{url}");
    }
}

internal sealed class FakeUrlPrompt : IUrlPrompt
{
    public string? NextUrl { get; set; }
    public List<(string Title, string Message)> Errors { get; } = new();
    public int PromptCalls { get; private set; }
    // StatusShown / StatusDisposed count ShowUrlStatus calls and handle disposals — StatusDisposed == StatusShown is the leak invariant (no overlay left stuck visible). LastCancellable / LastOnCancel capture the most recent Show's cancel affordance so tests can prove the Cancel button aborts the load.
    public int StatusShown { get; private set; }
    public int StatusDisposed { get; private set; }
    public bool LastCancellable { get; private set; }
    public Action? LastOnCancel { get; private set; }
    // Optional shared ordered log (also fed by UrlLoadCoordinatorTests' FakeUrlDownloader) for the "status shown before probe/classify" ordering tests.
    public List<string>? EventLog { get; set; }

    public Task<string?> PromptForUrlAsync(string title)
    {
        PromptCalls++;
        return Task.FromResult(NextUrl);
    }

    public void ShowError(string title, string message)
    {
        Errors.Add((title, message));
    }

    public IUrlStatusHandle ShowUrlStatus(string statusText, Action? onCancel)
    {
        StatusShown++;
        LastCancellable = onCancel != null;
        LastOnCancel = onCancel;
        EventLog?.Add("show");
        return new TrackingStatus(this);
    }

    private sealed class TrackingStatus : IUrlStatusHandle
    {
        private readonly FakeUrlPrompt owner;
        private bool disposed;

        public TrackingStatus(FakeUrlPrompt owner)
        {
            this.owner = owner;
        }

        public IProgress<UrlDownloadProgress> Progress { get; } = new Progress<UrlDownloadProgress>(_ => { });

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            owner.StatusDisposed++;
            owner.EventLog?.Add("hide");
        }
    }
}
