using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace FanRgbStudio;

public record SavedRgbZone(uint Index, string Name, uint Leds, int Start);
public record SavedRgbSetting(string Zone, string Mode, string Color, int Brightness, RgbCalibration Calibration, List<SavedRgbZone> Zones,int? SpeedPercent=null)
{
    public static SavedRgbSetting Capture(RgbDevice device, string zone, string mode, string hex, int brightness, RgbCalibration calibration,int? speedPercent=null)
    {
        var choice=RgbZoneChoice.For(device).SingleOrDefault(z=>z.Key==zone)??throw new InvalidDataException("저장할 RGB 구역이 없습니다.");
        var setting=new SavedRgbSetting(zone,OpenRgbClient.ZoneCompatibleMode(device,mode,choice.ZoneIds!=null),hex.ToUpperInvariant(),brightness,calibration,
            device.Zones?.Where(z=>choice.ZoneIds?.Contains(z.Index)==true).Select(z=>new SavedRgbZone(z.Index,z.Name,z.Leds,z.Start)).ToList()??[],speedPercent);
        setting.Validate(device);return setting;
    }
    public RgbZoneChoice Validate(RgbDevice device)
    {
        if(string.IsNullOrEmpty(Color)||!Regex.IsMatch(Color,"^#[0-9a-fA-F]{6}$")||Brightness is <0 or >100||Calibration==null||Zones==null||
            Calibration.Red is <0 or >200||Calibration.Green is <0 or >200||Calibration.Blue is <0 or >200)
            throw new InvalidDataException("저장된 RGB 색상 또는 밝기가 올바르지 않습니다.");
        var choice=RgbZoneChoice.For(device).SingleOrDefault(z=>z.Key==Zone)??throw new InvalidDataException("저장된 RGB 구역이 변경되었습니다.");
        var mode=device.Modes.SingleOrDefault(m=>m.Name==Mode)??throw new InvalidDataException("저장된 RGB 효과를 사용할 수 없습니다.");
        if(SpeedPercent is <0 or >100||SpeedPercent!=null&&(mode.Flags&1)==0)throw new InvalidDataException("저장된 효과 속도를 사용할 수 없습니다.");
        if(choice.ZoneIds!=null)
        {
            if(!mode.SupportsZoneColor||Zones.Count!=choice.ZoneIds.Length||Zones.Any(z=>z==null)||Zones.Select(z=>z.Index).Distinct().Count()!=Zones.Count||
                choice.ZoneIds.Any(id=>!Zones.Any(z=>z.Index==id&&device.Zones!.Any(current=>current.Index==id&&current.Name==z.Name&&current.Leds==z.Leds&&current.Start==z.Start))))
                throw new InvalidDataException("저장된 RGB 헤더 구성이 변경되었습니다.");
        }
        RgbWritePlan.Build(mode,OutputColor,Brightness,choice.ZoneIds!=null,SpeedPercent);
        return choice;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public Color OutputColor=>Calibration.Apply((Color)ColorConverter.ConvertFromString(Color));
}

public record SavedRgbDevice(string Identity, string Name, List<SavedRgbSetting> Settings)
{
    public RgbDevice Resolve(IReadOnlyList<RgbDevice> devices)
    {
        var device=devices.SingleOrDefault(d=>d.Identity==Identity)??throw new IOException(Name+": RGB 장치 감지 대기 중");
        if(Settings==null||Settings.Count is 0 or >32||Settings.Any(s=>s==null))throw new InvalidDataException("RGB 설정 형식 오류");
        // Validate every operation before the first write. A different device index is fine; identity must match.
        foreach(var setting in Settings)setting.Validate(device);
        return device;
    }
    public void Apply(OpenRgbClient client)
    {
        var device=Resolve(client.Devices());
        foreach(var setting in Settings)
            device=client.Apply(device,setting.Mode,setting.OutputColor,setting.Brightness,setting.Validate(device).ZoneIds,setting.SpeedPercent);
    }
}

public record RgbProfiles(int Version, string Machine, List<SavedRgbDevice> Devices)
{
    public static RgbProfiles Empty=>new(1,Environment.MachineName,[]);
    public RgbProfiles Upsert(RgbDevice device,SavedRgbSetting setting)
    {
        var previous=Devices.SingleOrDefault(d=>d.Identity==device.Identity);
        // A whole-device write supersedes all earlier zone settings. A zone write follows the existing baseline.
        var settings=setting.Zone=="all"?new List<SavedRgbSetting>():previous?.Settings.Where(s=>s.Zone!=setting.Zone).ToList()??[];
        settings.Add(setting);
        return this with{Devices=Devices.Where(d=>d.Identity!=device.Identity).Append(new(device.Identity,device.Name,settings)).ToList()};
    }
}

public sealed class RgbProfileStore(string path)
{
    public static RgbProfileStore Default=>new(Path.Combine(AppFiles.Data,"rgb-profiles.json"));
    public bool Exists=>File.Exists(path);
    public RgbProfiles Load()
    {
        if(!Exists)return RgbProfiles.Empty;
        var value=JsonSerializer.Deserialize<RgbProfiles>(File.ReadAllText(path));
        if(value==null||value.Version!=1||value.Machine!=Environment.MachineName||value.Devices==null||value.Devices.Count>32||
            value.Devices.Any(d=>d==null||string.IsNullOrEmpty(d.Identity))||value.Devices.Select(d=>d.Identity).Distinct().Count()!=value.Devices.Count)
            throw new InvalidDataException("RGB 설정의 PC 또는 파일 형식이 일치하지 않습니다.");
        return value;
    }
    public void Save(RgbProfiles value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);
    }
}

// Startup only: retry late discovery, then send once more after 10 seconds for delayed GPU initialization.
// A user's new setting cancels pending startup writes for that device.
public sealed class RgbRestoreQueue
{
    private record Entry(SavedRgbDevice Device,DateTime Due,int Passes=0);
    private readonly Dictionary<string,Entry> pending=new();
    private readonly DateTime deadline;
    public int PendingCount=>pending.Count;
    public int CompletedCount{get;private set;}
    public string Status{get;private set;}="저장된 RGB 설정 없음";
    public RgbRestoreQueue(RgbProfiles profile,DateTime now)
    {
        deadline=now.AddMinutes(2);
        foreach(var device in profile.Devices)pending[device.Identity]=new(device,now);
        if(pending.Count>0)Status="저장된 RGB 설정 자동 복원 대기 중";
    }
    public SavedRgbDevice? Due(DateTime now)
    {
        if(now>=deadline&&pending.Count>0){Status=$"RGB 자동 복원 시간 초과: {string.Join(", ",pending.Values.Select(p=>p.Device.Name))} · RGB 연결에서 다시 시도하세요.";pending.Clear();}
        return pending.Values.FirstOrDefault(e=>e.Due<=now)?.Device;
    }
    public void Succeeded(string identity,DateTime now)
    {
        if(!pending.TryGetValue(identity,out var entry))return;
        if(entry.Passes==0){pending[identity]=entry with{Passes=1,Due=now.AddSeconds(10)};Status=entry.Device.Name+": 적용 완료 · 시작 안정화 후 재확인 대기";}
        else{pending.Remove(identity);CompletedCount++;Status=entry.Device.Name+": 저장한 조명 자동 복원 완료";}
    }
    public void Failed(string identity,DateTime now,string message,bool retry=true)
    {
        if(!retry){pending.Remove(identity);Status="RGB 자동 복원 중지: "+message;return;}
        if(pending.TryGetValue(identity,out var entry))pending[identity]=entry with{Due=now.AddSeconds(5)};
        Status="RGB 자동 복원 대기: "+message;
    }
    public void Cancel(string identity)=>pending.Remove(identity);
}
