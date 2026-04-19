using Avalonia.Controls;
using Avalonia.Threading;
using Vomplayer.Controls;
using Vomplayer.Playback;
using Vomplayer.Services;
using Vomplayer.ViewModels;

namespace Vomplayer;

public partial class MainWindow : Window
{
    private readonly Playback.Playback playback;
    private readonly ViewModelMain viewModel;

    public string? InitialFile { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        var videoView = this.FindControl<VideoView>("VideoView")!;
        var sliderSeek = this.FindControl<Slider>("SliderSeek")!;

        playback = new Playback.Playback(a => Dispatcher.UIThread.Post(a));
        playback.Initialize();

        var filePicker = new FilePickerTopLevel(this);
        viewModel = new ViewModelMain(playback, filePicker);
        DataContext = viewModel;

        playback.AttachRenderSurface(videoView);
        videoView.RenderContextReady += OnVideoRenderContextReady;
        videoView.RenderFailed += OnVideoRenderFailed;

        sliderSeek.AddHandler(Slider.PointerPressedEvent, (_, _) => viewModel.OnSeekDragStart(), handledEventsToo: true);
        sliderSeek.AddHandler(Slider.PointerReleasedEvent, (_, _) => viewModel.OnSeekDragEnd(), handledEventsToo: true);

        Closed += OnClosed;
    }

    private void OnVideoRenderContextReady()
    {
        viewModel.InitialFile = InitialFile;
        viewModel.OnRenderContextReady();
    }

    private void OnVideoRenderFailed(int code)
    {
        System.Console.Error.WriteLine($"[vomplayer] mpv render failed with code {code}; video rendering stopped.");
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        viewModel.Dispose();
        playback.Dispose();
    }
}
