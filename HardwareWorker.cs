using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace FanRgbStudio;

// Hardware access lives in a separate process. A lost UI heartbeat releases owned controls.
public static class HardwareWorker
{
    private static IEnumerable<IHardware> Walk(IHardware hardware)
    {
        yield return hardware;
        foreach (var child in hardware.SubHardware) foreach (var item in Walk(child)) yield return item;
    }
    private static Computer Create() => new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true };
    private static Snapshot Read(Computer computer, Dictionary<string, FanPolicy> policies, string notice)
    {
        var sensors = new List<SensorReading>();
        var controls = new List<FanChannel>();
        string board = "메인보드 확인 중";
        foreach (var hardware in computer.Hardware.SelectMany(Walk))
        {
            hardware.Update();
            if (hardware.HardwareType == HardwareType.Motherboard) board = hardware.Name;
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control or SensorType.Load)
                    sensors.Add(new(sensor.Identifier.ToString(), sensor.Name, hardware.Name, sensor.SensorType.ToString(), sensor.Value is float v && float.IsFinite(v) ? v : null));
                if (sensor.SensorType == SensorType.Control && sensor.Control is { } c &&
                    hardware.HardwareType is HardwareType.SuperIO or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && c.MaxSoftwareValue >= Math.Max(30, c.MinSoftwareValue))
                    controls.Add(new(sensor.Identifier.ToString(), sensor.Name, hardware.Name, Math.Max(30, c.MinSoftwareValue), c.MaxSoftwareValue, sensor.Value,
                        policies.TryGetValue(sensor.Identifier.ToString(), out var policy) ? policy.Mode : "기본 제어"));
            }
        }
        return new(AppFiles.IsAdmin, board, sensors, controls, notice, DateTime.UtcNow);
    }
    public static float? SafetyTemperature(Snapshot snapshot)
    {
        var temps = snapshot.Sensors.Where(s => s.Type == "Temperature" && s.Value is > 0 and < 140 &&
            (s.Id.StartsWith("/amdcpu/") || s.Id.StartsWith("/intelcpu/"))).Select(s => s.Value!.Value).ToArray();
        // CPU temperature is required: do not silently substitute a motherboard or GPU reading.
        if (temps.Length == 0) return null;
        var gpu = snapshot.Sensors.Where(s => s.Type == "Temperature" && s.Id.StartsWith("/gpu-") && s.Value is > 0 and < 140).Select(s => s.Value!.Value);
        return temps.Concat(gpu).Max();
    }
    public static int Probe(string[] args)
    {
        var computer = Create();
        try
        {
            computer.Open();
            Thread.Sleep(1200);
            var json = JsonSerializer.Serialize(Read(computer, new(), "읽기 전용 진단"), new JsonSerializerOptions { WriteIndented = true });
            var at = Array.IndexOf(args, "--output");
            if (at >= 0 && at + 1 < args.Length) File.WriteAllText(args[at + 1], json);
            else using (var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }) output.WriteLine(json);
            return 0;
        }
        finally { computer.Close(); }
    }
    public static int Run(bool restoreSaved = true)
    {
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var computer = Create();
        var commands = new ConcurrentQueue<WorkerCommand>();
        var policies = new Dictionary<string, FanPolicy>();
        var touched = new Dictionary<string, IControl>();
        var heartbeat = Stopwatch.StartNew();
        var startup = Stopwatch.StartNew();
        var store = FanProfileStore.Default;
        FanProfile? pendingProfile = null;
        try { if (restoreSaved) pendingProfile = store.Load(); }
        catch (Exception ex) { AppFiles.Log("FAN PROFILE LOAD " + ex.Message); }
        bool eof = false, stop = false;
        string notice = "팬을 선택하고 적용하면 제어를 시작합니다.";
        void Restore()
        {
            foreach (var entry in touched.ToArray())
            {
                try { entry.Value.SetDefault(); touched.Remove(entry.Key); policies.Remove(entry.Key); }
                catch (Exception ex) { AppFiles.Log("RESTORE FAILED " + entry.Key + " " + ex.Message); }
            }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (await input.ReadLineAsync() is { } line)
                {
                    try { var cmd = JsonSerializer.Deserialize<WorkerCommand>(line); if (cmd != null) commands.Enqueue(cmd); } catch { }
                }
            }
            finally { Volatile.Write(ref eof, true); }
        });
        try
        {
            computer.Open();
            while (!stop && !Volatile.Read(ref eof))
            {
                var snapshot = Read(computer, policies, notice);
                var available = computer.Hardware.SelectMany(Walk).SelectMany(h => h.Sensors).Where(s => s.Control != null).ToDictionary(s => s.Identifier.ToString(), s => s.Control!);
                if(commands.Any(c=>c.Action is "set" or "restore"))pendingProfile=null;
                if (pendingProfile != null && snapshot.Admin && SafetyTemperature(snapshot) != null && snapshot.Controls.Count > 0)
                {
                    // Allow asynchronous sensor discovery to finish before validating all saved channels.
                    if (pendingProfile.Fans?.All(f => f != null && snapshot.Controls.Any(c => c.Id == f.Id)) == true || startup.Elapsed > TimeSpan.FromSeconds(15))
                    {
                        try { foreach (var command in pendingProfile.Commands(snapshot)) commands.Enqueue(command); }
                        catch (Exception ex) { notice = "팬 자동 복원 중지: " + ex.Message; AppFiles.Log(notice); }
                        pendingProfile = null;
                    }
                }
                bool dirty = false, applyFailed = false;
                while (commands.TryDequeue(out var command))
                {
                    if (command.Action == "heartbeat") { heartbeat.Restart(); continue; }
                    if (command.Action == "stop") { stop = true; break; }
                    if (command.Action == "restore")
                    {
                        pendingProfile = null; dirty = false;
                        try { store.Clear(); Restore(); notice = touched.Count == 0 ? "기본 제어로 복원하고 자동 복원 설정도 지웠습니다." : "일부 팬 복원 실패. 로그를 확인하세요."; }
                        catch (Exception ex) { Restore(); notice = "자동 복원 설정 삭제 실패: " + ex.Message; }
                        continue;
                    }
                    if (command.Action != "set") continue;
                    try
                    {
                        if (!AppFiles.IsAdmin) throw new InvalidOperationException("팬 제어는 관리자 권한으로 실행해 주세요.");
                        if (SafetyTemperature(snapshot) == null) throw new InvalidOperationException("CPU 온도를 읽을 수 없어 팬 제어를 시작하지 않습니다. 관리자 권한과 PawnIO를 확인하세요.");
                        if (!FanCurve.SupportedMode(command.Mode) || !float.IsFinite(command.Value)) throw new InvalidOperationException("올바르지 않은 팬 설정입니다.");
                        if(command.Mode=="사용자 곡선"&&!FanCurve.ValidateCustom(command.Curve,out var curveError))throw new InvalidOperationException(curveError);
                        var channel = snapshot.Controls.SingleOrDefault(c => c.Id == command.Id) ?? throw new InvalidOperationException("팬이 연결 해제되었습니다.");
                        if (!available.TryGetValue(channel.Id, out var control)) throw new InvalidOperationException("팬 제어기를 찾지 못했습니다.");
                        touched[channel.Id] = control;
                        policies[channel.Id] = new(command.Mode, Math.Clamp(command.Value, channel.Min, channel.Max),command.Mode=="사용자 곡선"?command.Curve!.ToList():null);
                        dirty = true;
                        notice = $"{channel.Name}: {command.Mode} 제어 중 · 85°C 이상이면 최대 출력";
                    }
                    catch (Exception ex) { notice = ex.Message; AppFiles.Log(notice); }
                }
                if (stop || heartbeat.Elapsed > TimeSpan.FromSeconds(10)) break;
                var temperature = SafetyTemperature(snapshot);
                if (temperature == null && policies.Count > 0)
                {
                    Restore(); applyFailed = true; notice = "CPU 온도 신호가 사라져 기본 팬 제어로 복원했습니다.";
                }
                foreach (var policy in policies.ToArray())
                {
                    var channel = snapshot.Controls.SingleOrDefault(c => c.Id == policy.Key);
                    if (channel == null || !available.TryGetValue(policy.Key, out var control))
                    {
                        Restore(); applyFailed = true; notice = "팬 연결이 변경되어 기본 제어로 복원했습니다."; break;
                    }
                    try { control.SetSoftware(FanCurve.Evaluate(policy.Value.Mode, policy.Value.Value, temperature!.Value, channel.Min, channel.Max,policy.Value.Curve)); }
                    catch (Exception ex) { Restore(); applyFailed = true; notice = "팬 제어 오류: " + ex.Message; break; }
                }
                if (dirty && !applyFailed)
                {
                    try { store.Save(FanProfile.Capture(snapshot, policies)); notice = $"{policies.Count}개 팬 설정 자동 저장 · 재실행 시 복원 · 85°C 이상 최대 출력"; }
                    catch (Exception ex) { notice = "팬 제어 중 · 설정 저장 실패: " + ex.Message; AppFiles.Log(notice); }
                }
                snapshot = snapshot with { Controls = snapshot.Controls.Select(c => c with { Mode = policies.TryGetValue(c.Id, out var p) ? p.Mode : "기본 제어" }).ToList(), Notice = notice,
                    ActiveFans = policies.Select(p => new FanPolicyEntry(p.Key, p.Value.Mode, p.Value.Value,p.Value.Curve?.ToList())).ToList() };
                output.WriteLine(JsonSerializer.Serialize(snapshot));
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex) { AppFiles.Log(ex.ToString()); try { output.WriteLine(JsonSerializer.Serialize(new Snapshot(AppFiles.IsAdmin, "장치 감지 실패", new(), new(), ex.Message, DateTime.UtcNow))); } catch { } return 1; }
        finally { Restore(); computer.Close(); }
        return 0;
    }
}
