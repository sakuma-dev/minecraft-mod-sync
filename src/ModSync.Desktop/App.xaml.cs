using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModSync.Platform.FileSystem;

namespace ModSync.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length != 0 && (e.Args.Length != 2 || e.Args[0] != "--smoke-test"))
        {
            Shutdown(2);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Length == 2)
        {
            // This developer-only path checks startup and rendering without starting Minecraft.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var smokeStarted = false;
            window.ContentRendered += async (_, _) =>
            {
                if (smokeStarted) return;
                smokeStarted = true;
                try
                {
                    var output = Path.GetFullPath(e.Args[1]);
                    Directory.CreateDirectory(output);
                    var surface = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
                    var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    image.Render(surface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    await using (var file = File.Create(Path.Combine(output, "startup.png")))
                        encoder.Save(file);

                    var assemblyHash = await new LocalFileHasher().ComputeSha256Async(typeof(App).Assembly.Location);
                    File.WriteAllText(Path.Combine(output, "startup.json"), JsonSerializer.Serialize(new
                    {
                        title = window.Title,
                        isLoaded = window.IsLoaded,
                        width = window.ActualWidth,
                        height = window.ActualHeight,
                        assemblySha256 = assemblyHash
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Shutdown(0);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    Shutdown(1);
                }
            };
        }
        window.Show();
    }
}
