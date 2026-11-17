using System.Windows.Media;

namespace FanRgbStudio;

public record RgbCalibration(int Red = 100, int Green = 100, int Blue = 100)
{
    public Color Apply(Color color)
    {
        static byte Scale(byte value, int gain) => (byte)Math.Clamp((int)Math.Round(value * Math.Clamp(gain, 0, 200) / 100.0), 0, 255);
        return Color.FromRgb(Scale(color.R, Red), Scale(color.G, Green), Scale(color.B, Blue));
    }
}
