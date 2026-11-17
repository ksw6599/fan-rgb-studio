using System.IO;
using System.Text.Json;

namespace FanRgbStudio;

public record SavedFan(string Id, string Hardware, string Mode, float Value, List<FanCurvePoint>? Curve=null);
public record FanProfile(int Version, string Machine, string Board, List<SavedFan> Fans)
{
    public static FanProfile Capture(Snapshot snapshot, IReadOnlyDictionary<string, FanPolicy> policies) =>
        new(1, Environment.MachineName, snapshot.Board, policies.Select(p =>
            new SavedFan(p.Key, snapshot.Controls.Single(c => c.Id == p.Key).Hardware, p.Value.Mode, p.Value.Value,p.Value.Curve?.ToList())).ToList());

    public WorkerCommand[] Commands(Snapshot current)
    {
        if (Version != 1 || Machine != Environment.MachineName || Board != current.Board || Fans == null || Fans.Count > 32 ||
            Fans.Any(f => f == null) || Fans.Select(f => f.Id).Distinct().Count() != Fans.Count)
            throw new InvalidDataException("저장된 팬 설정의 PC 또는 형식이 일치하지 않습니다.");
        foreach (var fan in Fans)
        {
            var channel = current.Controls.SingleOrDefault(c => c.Id == fan.Id && c.Hardware == fan.Hardware);
            if (channel == null || !FanCurve.SupportedMode(fan.Mode) ||
                !float.IsFinite(fan.Value) || fan.Value < channel.Min || fan.Value > channel.Max ||
                fan.Mode=="사용자 곡선"&&!FanCurve.ValidateCustom(fan.Curve,out _))
                throw new InvalidDataException("저장된 팬 채널 또는 출력이 현재 장치와 일치하지 않습니다.");
        }
        return Fans.Select(f => new WorkerCommand("set", f.Id, f.Mode, f.Value,f.Curve?.ToList())).ToArray();
    }
}

public sealed class FanProfileStore(string path)
{
    public static FanProfileStore Default => new(Path.Combine(AppFiles.Data, "fan-profile.json"));
    public FanProfile? Load() => File.Exists(path) ? JsonSerializer.Deserialize<FanProfile>(File.ReadAllText(path))
        ?? throw new InvalidDataException("팬 설정 파일이 비어 있습니다.") : null;
    public void Save(FanProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    // Only the explicit Restore command clears desired settings. Shutdown/fail-safe never calls this.
    public void Clear() => File.Delete(path);
}
