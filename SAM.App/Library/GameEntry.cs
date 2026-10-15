using System.Globalization;
using Microsoft.UI.Xaml.Media;
using SAM.App.Helpers;
using SAM.App.Localization;
using Windows.UI;

namespace SAM.App.Library;

/// <summary>A game in the library (successor of GameInfo from SAM.Picker).</summary>
public sealed class GameEntry : ObservableObject
{
    private string _name;
    private ImageSource? _image;
    private bool _imageRequested;
    private int _imageVersion;

    public GameEntry(uint id, string type, string? name, string? imageUrl)
    {
        Id = id;
        Type = type;
        _name = name ?? FallbackName(id);
        ImageUrl = imageUrl;
    }

    public uint Id { get; }

    public string Type { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value ?? FallbackName(Id)))
            {
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    public string? ImageUrl { get; set; }

    public ImageSource? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }

    public string TypeLabel => Type switch
    {
        "demo" => Strings.TypeDemo,
        "mod" => Strings.TypeMod,
        "junk" => Strings.TypeOther,
        _ => Strings.TypeGame,
    };

    public string Subtitle => Type == "normal"
        ? Strings.AppIdValue(Id.ToString(CultureInfo.InvariantCulture))
        : Strings.AppIdValue(Id.ToString(CultureInfo.InvariantCulture)) + " · " + TypeLabel;

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    /// <summary>Placeholder gradient while (or if) there is no capsule image - colour derived from the app ID.</summary>
    public Brush PlaceholderBrush
    {
        get
        {
            var hue = Id * 47 % 360;
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = FromHsl(hue, 0.45, 0.42), Offset = 0 },
                    new GradientStop { Color = FromHsl((hue + 40) % 360, 0.50, 0.28), Offset = 1 },
                },
            };
        }
    }

    /// <summary>Load the capsule image once the tile becomes visible.</summary>
    public async void EnsureImage()
    {
        if (_imageRequested)
        {
            return;
        }

        _imageRequested = true;
        var version = ++_imageVersion;
        var image = await ImageCache.LoadAsync(ImageUrl);

        if (version == _imageVersion)
        {
            Image = image;
        }
    }

    /// <summary>Release the image when the tile is recycled out of view - saves memory in large libraries.</summary>
    public void ReleaseImage()
    {
        _imageRequested = false;
        _imageVersion++;
        Image = null;
    }

    public override string ToString() => Name;

    private static string FallbackName(uint id) => Strings.AppFallbackName(id.ToString(CultureInfo.InvariantCulture));

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
