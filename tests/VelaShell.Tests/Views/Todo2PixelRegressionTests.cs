using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Security;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("Todo2PixelRegression")]
public sealed class Todo2PixelRegressionTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Todo2PixelRegressionTests).Assembly);

    [TestMethod]
    public void FocusedSftpPng_RejectsSaturatedLimePixels()
    {
        OnUi(() =>
        {
            ThemeVariant previousTheme = Application.Current!.RequestedThemeVariant;
            try
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                var viewModel = new ConnectionProfileViewModel
                {
                    Host = "files.example.com",
                    Port = 22,
                    Username = "root",
                    Password = SecureStringConvert.FromPlaintext("secret"),
                };
                viewModel.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
                var window = new ConnectionProfileView { DataContext = viewModel };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Button sftp = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(button => button.Classes.Contains("proto-tab")
                        && button.Content is TextBlock { Text: "SFTP" });
                Assert.IsTrue(sftp.Focus());
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(sftp.IsFocused);
                using WriteableBitmap bitmap = window.CaptureRenderedFrame()
                    ?? throw new AssertFailedException("Headless renderer did not produce a focused frame.");
                InspectFocusedProtocolStrip(bitmap);
                SaveOptionalFocusCapture(bitmap, "connection-profile-sftp-keyboard-focused-dark.png");
                window.Close();
            }
            finally
            {
                Application.Current.RequestedThemeVariant = previousTheme;
            }
        });
    }

    private static void InspectFocusedProtocolStrip(WriteableBitmap bitmap)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        const int stride = 4;
        int bufferSize = checked(width * height * stride);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bufferSize, width * stride);
            int limePixels = 0;
            int purplePixels = 0;
            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            var limeColors = new Dictionary<string, int>();
            var purpleColors = new Dictionary<string, int>();
            // Fixed protocol strip sample excludes tab text and the selected underline.
            for (int y = 52; y < 78 && y < height; y++)
            {
                for (int x = 72; x < 174 && x < width; x++)
                {
                    int offset = (y * width + x) * stride;
                    byte blue = Marshal.ReadByte(buffer, offset);
                    byte green = Marshal.ReadByte(buffer, offset + 1);
                    byte red = Marshal.ReadByte(buffer, offset + 2);
                    if (green > 180 && red > 100 && blue < 100)
                    {
                        limePixels++;
                        string color = $"#{red:X2}{green:X2}{blue:X2}";
                        limeColors[color] = limeColors.GetValueOrDefault(color) + 1;
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                    if (red > 80 && blue > 100 && green < 190)
                    {
                        purplePixels++;
                        string color = $"#{red:X2}{green:X2}{blue:X2}";
                        purpleColors[color] = purpleColors.GetValueOrDefault(color) + 1;
                    }
                }
            }

            Assert.AreEqual(0, limePixels,
                $"Focused SFTP frame contains saturated Fluent lime pixels at {minX}..{maxX}, {minY}..{maxY}. Colors: {string.Join(", ", limeColors.Keys)}");
            Assert.IsGreaterThan(0, purplePixels, "Focused SFTP capture lacks expected Vela purple/accent pixels.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SaveOptionalFocusCapture(WriteableBitmap bitmap, string fileName)
    {
        string? configured = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }
        string directory = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(RepositoryRoot, configured);
        Directory.CreateDirectory(directory);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VelaShell.slnx")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
