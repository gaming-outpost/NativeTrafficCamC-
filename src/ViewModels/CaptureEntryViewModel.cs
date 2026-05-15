using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CoastalCommandCenter.ViewModels;

public partial class CaptureEntryViewModel : ObservableObject
{
    // Matches: {prefix}_{yyyyMMdd}_{HHmmssfff}
    private static readonly Regex TimestampSuffix =
        new(@"^(.+?)_(\d{8})_(\d{9})$", RegexOptions.Compiled);

    [ObservableProperty] private Bitmap? _thumbnail;

    public string FilePath { get; }
    public string CameraName { get; }
    public string Region { get; }
    public DateTime Timestamp { get; }
    public string DisplayTimestamp { get; }

    public CaptureEntryViewModel(string filePath)
    {
        FilePath = filePath;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var m = TimestampSuffix.Match(stem);

        if (m.Success)
        {
            var prefix = m.Groups[1].Value;
            var sep = prefix.IndexOf('_');
            Region = sep >= 0 ? prefix[..sep] : prefix;
            CameraName = (sep >= 0 ? prefix[(sep + 1)..] : prefix).Replace('_', ' ');

            if (DateTime.TryParseExact(
                    m.Groups[2].Value + m.Groups[3].Value,
                    "yyyyMMddHHmmssfff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var ts))
            {
                Timestamp = ts;
                DisplayTimestamp = ts.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.CurrentCulture);
            }
            else
            {
                Timestamp = DateTime.MinValue;
                DisplayTimestamp = "Unknown";
            }
        }
        else
        {
            Region = "UNKNOWN";
            CameraName = stem.Replace('_', ' ');
            Timestamp = DateTime.MinValue;
            DisplayTimestamp = "Unknown";
        }
    }

    public async Task LoadThumbnailAsync()
    {
        if (Thumbnail != null || !File.Exists(FilePath)) return;
        try
        {
            var bmp = await Task.Run(() => new Bitmap(FilePath));
            await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bmp);
        }
        catch
        {
            // Ignore load failures — thumbnail stays null.
        }
    }
}
