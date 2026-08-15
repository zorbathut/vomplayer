using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;

namespace Vomplayer.Tests;

// Shared IPlayback fake for every suite that drives a ViewModelMain / VideoContext / coordinator against a scripted playback. Superset of the per-suite copies it replaced: every method records its calls (lists for argument-carrying calls, counters otherwise), every IPlayback observable is a settable [ObservableProperty] so tests can simulate mpv property echoes, and Raise* helpers fire the IPlayback events.
internal sealed partial class FakePlayback : ObservableObject, IPlayback
{
    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private bool isPaused = true;

    [ObservableProperty]
    private bool isSeeking;

    // Faithful default: mpv's core-idle is true before any file is loaded. Drift-controller tests that need "actually advancing" set this to false explicitly.
    [ObservableProperty]
    private bool isCoreIdle = true;

    [ObservableProperty]
    private bool isEofReached;

    [ObservableProperty]
    private double volume = 100;

    [ObservableProperty]
    private bool isMuted;

    [ObservableProperty]
    private IReadOnlyList<MediaTrack> videoTracks = Array.Empty<MediaTrack>();

    [ObservableProperty]
    private IReadOnlyList<MediaTrack> audioTracks = Array.Empty<MediaTrack>();

    [ObservableProperty]
    private IReadOnlyList<MediaTrack> subtitleTracks = Array.Empty<MediaTrack>();

    [ObservableProperty]
    private IReadOnlyList<MediaChapter> chapters = Array.Empty<MediaChapter>();

    [ObservableProperty]
    private int? currentVideoId;

    [ObservableProperty]
    private int? currentAudioId;

    [ObservableProperty]
    private int? currentSubtitleId;

    [ObservableProperty]
    private double? videoAspect;

    [ObservableProperty]
    private double? videoFps;

    [ObservableProperty]
    private double? estimatedVfFps;

    [ObservableProperty]
    private string? mediaTitle;

    public event Action? FileLoaded;
    public event Action? TracksReloaded;
    public event Action<bool>? SourceHdrChanged;
    public event Action<bool>? IsSourceFpsTrustedChanged;

    public bool IsSourceHdr { get; set; }
    public string? HwdecCurrent { get; set; }
    public IReadOnlyList<string> HwdecTranscript { get; set; } = Array.Empty<string>();
    public bool IsSourceFpsTrusted { get; set; } = true;
    public string FpsTrustReason { get; set; } = "";

    public int InitializeCalls { get; private set; }
    // Mutable from tests so post-load assertions can isolate per-step deltas (the playlist tests reset these between LoadPaths and a follow-up auto-advance to assert "this trigger ALONE produced one new load").
    public int LoadFileCalls { get; set; }
    public string? LastLoadedFile { get; set; }
    public List<string> LoadedFiles { get; } = new();
    // Full (path, startPaused) argument log — the autosave restore tests assert the startPaused intent threads through to LoadFile atomically.
    public List<(string Path, bool StartPaused)> LoadFileArgs { get; } = new();
    public int TogglePauseCalls { get; private set; }
    public List<bool> SetPausedCalls { get; } = new();
    public double? LastSeekSeconds { get; private set; }
    public List<double> SeekCalls { get; } = new();
    public List<double> SeekRelativeCalls { get; } = new();
    public int StepFrameForwardCalls { get; private set; }
    public int StepFrameBackCalls { get; private set; }
    public string? LastLoadedAudio { get; private set; }
    public string? LastLoadedSubtitle { get; private set; }
    public List<int?> VideoSelections { get; } = new();
    public List<int?> AudioSelections { get; } = new();
    public List<int?> SubtitleSelections { get; } = new();
    public List<double> SetVolumeCalls { get; } = new();
    public List<double> AdjustVolumeCalls { get; } = new();
    public int ToggleMuteCalls { get; private set; }
    public List<double> SetSpeedCalls { get; } = new();
    public int EnableHdrOutputCalls { get; private set; }
    public int DisableHdrOutputCalls { get; private set; }
    public List<double> SetFrameMultiplierCalls { get; } = new();
    public int ClearFrameMultiplierCalls { get; private set; }

    public void Initialize()
    {
        InitializeCalls++;
    }

    public void LoadFile(string path, bool startPaused)
    {
        LoadFileCalls++;
        LastLoadedFile = path;
        LoadedFiles.Add(path);
        LoadFileArgs.Add((path, startPaused));
        // Mirror production Playback.LoadFile, which synchronously clears IsEofReached so a stale carry-over from the prior file can't trip the auto-advance rising-edge gate. The synchronous PropertyChanged this fires is what re-enters the eof handler, so faking it here is necessary for the multi-advance scenarios to behave like production.
        IsEofReached = false;
    }

    // Real Playback.TogglePause writes to mpv asynchronously — IsPaused only changes when mpv echoes it back via OnMpvPropertyChanged. The fake elides that latency intentionally; tests asserting on rapid-toggle race behavior would need to add an async-ish fake.
    public void TogglePause()
    {
        TogglePauseCalls++;
        IsPaused = !IsPaused;
    }

    // Synchronous mirror update is the IPlayback.SetPaused contract (matches real Playback), not a test convenience — the coordinator's sync-broadcast reads IsPaused at gesture time.
    public void SetPaused(bool paused)
    {
        SetPausedCalls.Add(paused);
        IsPaused = paused;
    }

    // Records the seek but does NOT move PositionSeconds — mirroring real Playback, where the position only changes when mpv echoes it back. Tests that need the post-seek position set PositionSeconds themselves.
    public void Seek(double seconds)
    {
        LastSeekSeconds = seconds;
        SeekCalls.Add(seconds);
    }

    public void SeekRelative(double seconds)
    {
        SeekRelativeCalls.Add(seconds);
    }

    public void StepFrameForward()
    {
        StepFrameForwardCalls++;
    }

    public void StepFrameBack()
    {
        StepFrameBackCalls++;
    }

    public void LoadAudio(string path)
    {
        LastLoadedAudio = path;
    }

    public void LoadSubtitle(string path)
    {
        LastLoadedSubtitle = path;
    }

    public void SetVideo(int? trackId)
    {
        VideoSelections.Add(trackId);
    }

    public void SetAudio(int? trackId)
    {
        AudioSelections.Add(trackId);
    }

    public void SetSubtitle(int? trackId)
    {
        SubtitleSelections.Add(trackId);
    }

    // Mirror real Playback's behavior at the fake's seam: SetVolume tracks the request and applies it to the observable so VM mirrors update synchronously. AdjustVolume logs the delta separately so tests can distinguish "user dragged the slider" (SetVolume) from "user pressed VolumeUp" (AdjustVolume).
    public void SetVolume(double percent)
    {
        SetVolumeCalls.Add(percent);
        Volume = percent;
    }

    // Real Playback.AdjustVolume issues mpv's `add volume <delta>` command — no local read-modify-write, so the cached `Volume` doesn't change here either. Tests that want to observe the post-adjust value should set `Volume` themselves to simulate the mpv echo.
    public void AdjustVolume(double deltaPercent)
    {
        AdjustVolumeCalls.Add(deltaPercent);
    }

    public void ToggleMute()
    {
        ToggleMuteCalls++;
        IsMuted = !IsMuted;
    }

    public void SetSpeed(double rate)
    {
        SetSpeedCalls.Add(rate);
    }

    public void EnableHdrOutput()
    {
        EnableHdrOutputCalls++;
    }

    public void DisableHdrOutput()
    {
        DisableHdrOutputCalls++;
    }

    public void SetFrameMultiplier(double outputFps)
    {
        SetFrameMultiplierCalls.Add(outputFps);
    }

    public void ClearFrameMultiplier()
    {
        ClearFrameMultiplierCalls++;
    }

    public void RaiseFileLoaded()
    {
        FileLoaded?.Invoke();
    }

    public void RaiseTracksReloaded()
    {
        TracksReloaded?.Invoke();
    }

    public void RaiseSourceHdrChanged(bool isHdr)
    {
        IsSourceHdr = isHdr;
        SourceHdrChanged?.Invoke(isHdr);
    }

    public void RaiseIsSourceFpsTrustedChanged(bool trusted)
    {
        IsSourceFpsTrusted = trusted;
        IsSourceFpsTrustedChanged?.Invoke(trusted);
    }

    public void Dispose()
    {
    }
}
