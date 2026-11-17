using System.IO;
using System.Text.Json;

namespace FanRgbStudio;

// Legacy one-time upgrade handover. Normal launches now use the durable FanProfile store.
public record FanSession(string Board,DateTime CreatedUtc,List<FanPolicyEntry> Fans)
{
    public WorkerCommand[] Commands(Snapshot current)
    {
        if(DateTime.UtcNow-CreatedUtc>TimeSpan.FromMinutes(10)||CreatedUtc>DateTime.UtcNow.AddSeconds(5)||Board!=current.Board)
            throw new InvalidDataException("업데이트 전 팬 설정의 PC 또는 시각이 일치하지 않습니다.");
        if(Fans.Count>32||Fans.Select(f=>f.Id).Distinct().Count()!=Fans.Count)throw new InvalidDataException("팬 세션 항목이 올바르지 않습니다.");
        foreach(var fan in Fans)
        {
            if(!FanCurve.SupportedMode(fan.Mode)||!float.IsFinite(fan.Value)||fan.Value<30||fan.Value>100||!current.Controls.Any(c=>c.Id==fan.Id)||fan.Mode=="사용자 곡선"&&!FanCurve.ValidateCustom(fan.Curve,out _))
                throw new InvalidDataException("팬 세션의 채널 또는 출력이 올바르지 않습니다.");
        }
        return Fans.Select(f=>new WorkerCommand("set",f.Id,f.Mode,f.Value,f.Curve?.ToList())).ToArray();
    }
    public static FanSession? Load(string? path)
    {
        if(string.IsNullOrEmpty(path))return null;
        try{return JsonSerializer.Deserialize<FanSession>(File.ReadAllText(path));}
        catch(Exception ex){AppFiles.Log("FAN SESSION "+ex.Message);return null;}
    }
}
public record FanPolicyEntry(string Id,string Mode,float Value,List<FanCurvePoint>? Curve=null);
