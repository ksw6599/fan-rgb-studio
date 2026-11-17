using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace FanRgbStudio;

public record SensorReading(string Id, string Name, string Hardware, string Type, float? Value);
public record FanChannel(string Id, string Name, string Hardware, float Min, float Max, float? Value, string Mode);
public record Snapshot(bool Admin, string Board, List<SensorReading> Sensors, List<FanChannel> Controls, string Notice, DateTime Timestamp, List<FanPolicyEntry>? ActiveFans = null);
public record WorkerCommand(string Action, string Id = "", string Mode = "", float Value = 50, List<FanCurvePoint>? Curve = null);
public record FanPolicy(string Mode, float Value, List<FanCurvePoint>? Curve = null);
public record FanCurvePoint(float Temperature, float Duty);
public record Preferences(string Color = "#53D9C5", int Brightness = 70, string Effect = "단색", string FanMode = "균형", double Manual = 50,
    string DisplayColor = "#53D9C5", int DisplayBrightness = 70, string DisplayEffect = "온도에 따라", bool CpuTemp = true, bool CpuLoad = true, bool GpuTemp = true, bool GpuLoad = true,string RgbIdentity="",string RgbZone="all", Dictionary<string,RgbCalibration>? RgbCalibrations=null, List<FanCurvePoint>? CustomCurve=null);

public static class AppFiles
{
    public static string Version=>typeof(AppFiles).Assembly.GetName().Version?.ToString(3)??"0.0.0";
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunuongFanStudio");
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void Log(string message)
    {
        try { Directory.CreateDirectory(Data); File.AppendAllText(Path.Combine(Data, "studio.log"), $"{DateTime.Now:O} {message}\n"); } catch { }
    }
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Data, "preferences.json"))) ?? new(); } catch { return new(); }
    }
    public static void Save(Preferences prefs)
    {
        Directory.CreateDirectory(Data);
        var path = Path.Combine(Data, "preferences.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public static class FanCurve
{
    public static List<FanCurvePoint> DefaultCustom=>[new(30,30),new(40,35),new(50,45),new(60,60),new(70,75),new(80,90),new(85,100)];
    public static bool SupportedMode(string mode)=>mode is "저소음" or "균형" or "성능" or "수동" or "사용자 곡선";
    public static bool ValidateCustom(IReadOnlyList<FanCurvePoint>? points,out string error)
    {
        error="";
        if(points==null||points.Count is <2 or >12){error="온도 지점은 2~12개로 설정해 주세요.";return false;}
        for(int i=0;i<points.Count;i++)
        {
            var point=points[i];
            if(point==null||!float.IsFinite(point.Temperature)||!float.IsFinite(point.Duty)||point.Temperature is <20 or >85||point.Duty is <30 or >100)
            {error="온도는 20~85°C, 출력은 30~100% 사이로 입력해 주세요.";return false;}
            if(i>0&&(point.Temperature<=points[i-1].Temperature||point.Duty<points[i-1].Duty))
            {error="온도는 위에서 아래로 높아져야 하고, 출력은 이전 지점보다 낮출 수 없습니다.";return false;}
        }
        if(points[^1].Temperature!=85||points[^1].Duty!=100){error="마지막 보호 지점은 85°C / 100%로 설정해 주세요.";return false;}
        return true;
    }
    public static float Evaluate(string mode, float manual, float temperature, float min = 30, float max = 100, IReadOnlyList<FanCurvePoint>? custom=null)
    {
        if (!float.IsFinite(temperature) || temperature <= 0 || temperature >= 85) return max;
        if(mode=="사용자 곡선")
        {
            if(!ValidateCustom(custom,out _))return max;
            float duty=custom![0].Duty;
            for(int i=1;temperature>custom[0].Temperature&&i<custom.Count;i++)
                if(temperature<=custom[i].Temperature)
                {
                    var a=custom[i-1];var b=custom[i];duty=a.Duty+(b.Duty-a.Duty)*(temperature-a.Temperature)/(b.Temperature-a.Temperature);break;
                }
            return Math.Clamp(duty,Math.Max(30,min),max);
        }
        float[] temperatures = [30, 45, 60, 75, 85];
        float[] speeds = mode switch
        {
            "저소음" => [35, 40, 55, 80, 100],
            "성능" => [50, 65, 80, 95, 100],
            _ => [40, 50, 65, 85, 100]
        };
        float result = speeds[0];
        if (mode == "수동") result = manual;
        else if (temperature > temperatures[0]) for (int i = 1; i < temperatures.Length; i++)
        {
            if (temperature <= temperatures[i])
            {
                result = speeds[i - 1] + (speeds[i] - speeds[i - 1]) * (temperature - temperatures[i - 1]) / (temperatures[i] - temperatures[i - 1]);
                break;
            }
        }
        return Math.Clamp(result, Math.Max(30, min), max);
    }
}
