using System.IO;
using System.Text.Json;

namespace FanRgbStudio;

public static class RgbDiagnostics
{
    // Deliberately omit serial numbers, USB paths, user/machine names and unrelated sensors.
    public static string Serialize(IReadOnlyList<RgbDevice> devices,string engineState,string? lastError) => JsonSerializer.Serialize(new
    {
        Version=AppFiles.Version,Timestamp=DateTime.UtcNow,Engine=engineState,LastError=lastError,
        Devices=devices.Select(d=>new
        {
            d.Name,d.Vendor,d.DeviceType,d.Leds,ActiveEffect=d.Modes.FirstOrDefault(m=>m.Index==d.ActiveMode)?.Name,
            Modes=d.Modes.Select(m=>new{m.Name,m.Flags,m.SupportsColor,m.SupportsZoneColor,m.SupportsSpeed,m.SupportsBrightness,ColorMode=m.Field(11),SpeedMinimum=m.Field(2),SpeedMaximum=m.Field(3),Speed=m.Field(8)}),
            Zones=d.Zones?.Select(z=>new{z.Name,z.Type,z.Leds,z.Minimum,z.Maximum})
        })
    },new JsonSerializerOptions{WriteIndented=true});
    public static void Save(string path,IReadOnlyList<RgbDevice> devices,string engineState,string? error)
    {Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path,Serialize(devices,engineState,error));}
}
