using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

[TestFixture]
public partial class ViewModelMainTests
{
    private sealed partial class FakePlayback : ObservableObject, IPlayback
    {
        [ObservableProperty]
        private double positionSeconds;

        [ObservableProperty]
        private double durationSeconds;

        [ObservableProperty]
        private bool isPaused = true;

        [ObservableProperty]
        private bool isSeeking;

        public event Action? FileLoaded;
        public event Action<int>? FileEnded;

        public int InitializeCalls { get; private set; }
        public int TogglePauseCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int LoadFileCalls { get; private set; }
        public string? LastLoadedFile { get; private set; }
        public double? LastSeekSeconds { get; private set; }

        public void Initialize()
        {
            InitializeCalls++;
        }

        public void LoadFile(string path)
        {
            LoadFileCalls++;
            LastLoadedFile = path;
        }

        // Real Playback.TogglePause writes to mpv asynchronously — IsPaused only changes when mpv echoes it back via OnMpvPropertyChanged. The fake elides that latency intentionally; tests asserting on rapid-toggle race behavior would need to add an async-ish fake.
        public void TogglePause()
        {
            TogglePauseCalls++;
            IsPaused = !IsPaused;
        }

        public void Stop()
        {
            StopCalls++;
        }

        public void Seek(double seconds)
        {
            LastSeekSeconds = seconds;
        }

        public void RaiseFileLoaded()
        {
            FileLoaded?.Invoke();
        }

        public void RaiseFileEnded(int reason)
        {
            FileEnded?.Invoke(reason);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeFilePicker : IFilePicker
    {
        public string? NextResult { get; set; }
        public int Calls { get; private set; }

        public Task<string?> PickVideoFileAsync(string title)
        {
            Calls++;
            return Task.FromResult(NextResult);
        }
    }

    [Test]
    public void NullPlaybackThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(null!, new FakeFilePicker()));
    }

    [Test]
    public void NullFilePickerThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewModelMain(new FakePlayback(), null!));
    }

    [Test]
    public void PositionMirrorsPlaybackPositionSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.PositionSeconds = 12.5;
        Assert.That(vm.Position, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
    }

    [Test]
    public void DurationMirrorsPlaybackDurationSeconds()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.DurationSeconds = 60;
        Assert.That(vm.Duration, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public void IsPausedMirrorsPlayback()
    {
        var pb = new FakePlayback { IsPaused = true };
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.IsPaused = false;
        Assert.That(vm.IsPaused, Is.False);
    }

    [Test]
    public void SeekValueTracksPlaybackWhenNotDragging()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.DurationSeconds = 100;
        pb.PositionSeconds = 25;
        Assert.That(vm.SeekValue, Is.EqualTo(0.25));
    }

    [Test]
    public void SeekToSeeksToNormalizedPositionScaledByDuration()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.DurationSeconds = 100;

        vm.SeekTo(0.4);

        Assert.That(pb.LastSeekSeconds, Is.EqualTo(40));
    }

    [Test]
    public void SeekValueTracksPlaybackPosition()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.DurationSeconds = 100;

        pb.PositionSeconds = 75;

        Assert.That(vm.SeekValue, Is.EqualTo(0.75));
    }

    [Test]
    public void PlayPauseCommandTogglesPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        vm.PlayPauseCommand.Execute(null);
        Assert.That(pb.TogglePauseCalls, Is.EqualTo(1));
    }

    [Test]
    public void StopCommandStopsPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        vm.StopCommand.Execute(null);
        Assert.That(pb.StopCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task OpenCommandLoadsPickedFile()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = "/path/to/video.mp4" };
        var vm = new ViewModelMain(pb, picker);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(picker.Calls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/video.mp4"));
    }

    [Test]
    public async Task OpenCommandIgnoresCancelledPicker()
    {
        var pb = new FakePlayback();
        var picker = new FakeFilePicker { NextResult = null };
        var vm = new ViewModelMain(pb, picker);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.That(picker.Calls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.Null);
    }

    [Test]
    public void OnRenderContextReadyLoadsInitialFileOnce()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker())
        {
            InitialFile = "/path/to/initial.mp4",
        };

        vm.OnRenderContextReady();
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
        Assert.That(pb.LastLoadedFile, Is.EqualTo("/path/to/initial.mp4"));

        // A second fire (e.g. detach/reattach) must not reload.
        vm.OnRenderContextReady();
        Assert.That(pb.LoadFileCalls, Is.EqualTo(1));
    }

    [Test]
    public void OnRenderContextReadyWithNoInitialFileNoOps()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        vm.OnRenderContextReady();
        Assert.That(pb.LastLoadedFile, Is.Null);
    }

    [Test]
    public void DisposeUnsubscribesFromPlayback()
    {
        var pb = new FakePlayback();
        var vm = new ViewModelMain(pb, new FakeFilePicker());
        pb.PositionSeconds = 5;
        var beforeDispose = vm.Position;

        vm.Dispose();
        pb.PositionSeconds = 99;
        Assert.That(vm.Position, Is.EqualTo(beforeDispose));
    }
}
