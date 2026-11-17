using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HidSharp;

namespace FanRgbStudio;

public static class DeviceDiagnostics
{
    // Explicit maintenance action: applies only the saved identity/zone through an already running engine.
    public static int ApplySavedRgb(string[] args)
    {
        try
        {
            var prefs=AppFiles.Load();using var client=new OpenRgbClient();
            string? Argument(string key){int at=Array.IndexOf(args,key);return at>=0&&at+1<args.Length?args[at+1]:null;}
            var targetName=Argument("--rgb-device");var targetZone=Argument("--rgb-zone");
            var device=client.Devices().Single(d=>targetName!=null?d.Name==targetName:d.Identity==prefs.RgbIdentity);
            var choice=RgbZoneChoice.For(device).Single(z=>z.Key==(targetZone??prefs.RgbZone));
            if(!System.Text.RegularExpressions.Regex.IsMatch(prefs.Color,"^#[0-9a-fA-F]{6}$"))throw new InvalidDataException("Invalid HEX");
            var input=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(prefs.Color);
            var calibration=prefs.RgbCalibrations?.GetValueOrDefault(device.Identity)??new();var corrected=calibration.Apply(input);
            var result=client.Apply(device,Argument("--rgb-mode")??prefs.Effect,corrected,prefs.Brightness,choice.ZoneIds);
            Thread.Sleep(5000);
            File.WriteAllText(Output(args),JsonSerializer.Serialize(new{Device=result.Name,Zone=choice.Label,Input=prefs.Color,Corrected=$"#{corrected.R:X2}{corrected.G:X2}{corrected.B:X2}",prefs.Brightness,Calibration=calibration,SdkReadback=true,PhysicalColorConfirmed=false},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception ex){File.WriteAllText(Output(args),JsonSerializer.Serialize(new{Error=ex.ToString()}));return 1;}
    }
    public static int Inventory(string[] args)
    {
        var devices = DeviceList.Local.GetHidDevices().Select(d =>
        {
            string product; try { product = d.GetProductName(); } catch { product = ""; }
            return new { Vid = d.VendorID.ToString("X4"), Pid = d.ProductID.ToString("X4"), Product = product, d.DevicePath, Input = d.GetMaxInputReportLength(), Output = d.GetMaxOutputReportLength() };
        }).ToList();
        File.WriteAllText(Output(args), JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true })); return 0;
    }
    public static string Output(string[] args) { int at = Array.IndexOf(args, "--output"); return at >= 0 && at + 1 < args.Length ? args[at + 1] : Path.Combine(AppFiles.Data, "device-report.json"); }
    public static int DisplayDebug(string[] args)
    {
        string output=Output(args);var notices=new List<string>();
        try
        {
            string snapshotPath=output+".sensors.json";
            int sourceAt=Array.IndexOf(args,"--snapshot-source");
            if(sourceAt>=0&&sourceAt+1<args.Length)
            {
                // Reuse fresh telemetry already written by the approved, running sensor process.
                // This diagnostic path does not access or elevate another process.
                Snapshot? live=null;
                for(int n=0;n<3;n++)try{live=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(args[sourceAt+1]));if(live!=null)break;}catch(IOException){Thread.Sleep(50);}catch(JsonException){Thread.Sleep(50);}
                if(live==null||!live.Admin||DateTime.UtcNow-live.Timestamp>TimeSpan.FromSeconds(5)||live.Timestamp>DateTime.UtcNow.AddSeconds(2))throw new IOException("관리자 센서 앱의 최신 측정값을 확인하지 못했습니다.");
                File.WriteAllText(snapshotPath,JsonSerializer.Serialize(live,new JsonSerializerOptions{WriteIndented=true}));
            }
            else
            {
                if(!AppFiles.IsAdmin)throw new InvalidOperationException("CPU 온도 실기기 검증에는 관리자 권한 또는 실행 중인 센서 앱의 최신 진단 파일이 필요합니다.");
                HardwareWorker.Probe(["--probe","--output",snapshotPath]);
            }
            var snapshot=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(snapshotPath))!;
            var metrics=DisplayMetrics.From(snapshot);
            if(metrics.CpuTemperature==null)throw new IOException("CPU 온도를 읽지 못했습니다.");
            var settings=new DisplayOptions(System.Windows.Media.Color.FromRgb(83,217,197),70,false,true,true,true,true);
            using var display=new Pa120Display(settings);display.Status+=s=>{lock(notices)notices.Add(s);};display.Update(metrics,settings);display.Start();
            int durationAt=Array.IndexOf(args,"--duration");int duration=durationAt>=0&&durationAt+1<args.Length&&int.TryParse(args[durationAt+1],out var seconds)?Math.Clamp(seconds,4,600):4;
            var clock=Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromSeconds(duration))
            {
                if(sourceAt>=0&&sourceAt+1<args.Length)
                {
                    try
                    {
                        var fresh=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(args[sourceAt+1]));
                        if(fresh is {Admin:true}&&DateTime.UtcNow-fresh.Timestamp<TimeSpan.FromSeconds(5)){metrics=DisplayMetrics.From(fresh);display.Update(metrics,settings);}
                    }
                    catch(IOException){}catch(JsonException){}
                }
                string[] updates;lock(notices)updates=notices.ToArray();
                File.WriteAllText(output,JsonSerializer.Serialize(new{Admin=AppFiles.IsAdmin,Metrics=metrics,Reports=updates,StillRunning=display.Running},new JsonSerializerOptions{WriteIndented=true}));
                if(!display.Running)break;
                Thread.Sleep(250);
            }
            display.Dispose();
            File.WriteAllText(output,JsonSerializer.Serialize(new{Admin=AppFiles.IsAdmin,Metrics=metrics,Reports=notices.ToArray(),StillRunning=display.Running},new JsonSerializerOptions{WriteIndented=true}));
            return notices.Any(s=>s.Contains("전송 중"))?0:1;
        }
        catch(Exception ex){File.WriteAllText(output,JsonSerializer.Serialize(new{Error=ex.ToString(),Reports=notices.ToArray()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
    public static int RgbDebug(string[] args)
    {
        Process? process = null; var logs = new List<string>();
        try
        {
            bool already = false; try { using var c = new OpenRgbClient(); c.Devices(); already = true; } catch { }
            if (!already)
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "tools", "OpenRGB", "OpenRGB.exe");
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--server", "--server-host", "127.0.0.1", "--server-port", "6742", "--noautoconnect", "--config", Path.Combine(AppFiles.Data, "OpenRGB") }) start.ArgumentList.Add(arg);
                process = new Process { StartInfo = start }; process.OutputDataReceived += (_, e) => { if(e.Data != null) lock(logs) logs.Add(e.Data); }; process.ErrorDataReceived += (_, e) => { if(e.Data != null) lock(logs) logs.Add(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                Thread.Sleep(5000);
                if(process.HasExited) throw new IOException("OpenRGB exited: " + process.ExitCode);
            }
            using var client = new OpenRgbClient();
            var devices = client.Devices();
            if (args.Contains("--apply-test"))
            {
                var gpu = devices.Single(d => d.Name == "MSI GeForce RTX 4070 Gaming X Trio");
                var watch = Stopwatch.StartNew();
                var confirmed=client.Apply(gpu, "Direct", System.Windows.Media.Color.FromRgb(83,217,197), 70);
                logs.Add("MSI Direct mint 70% applied; mode="+confirmed.ActiveMode+" colors="+string.Join(",",confirmed.CurrentColors??[]) + "; verified in " + watch.ElapsedMilliseconds + "ms");
                Thread.Sleep(5000); logs.Add("Kept engine alive for 5 seconds after SDK readback so queued hardware writes can complete.");
            }
            if(args.Contains("--casefan-test"))
            {
                var board=devices.Single(d=>d.Name=="ASRock B650M PG Lightning");
                var zones=board.Zones?.Where(z=>z.Addressable&&z.Leds>0).Select(z=>z.Index).ToArray();
                var confirmed=client.Apply(board,"Static",System.Windows.Media.Color.FromRgb(83,217,197),70,zones?.Length>0?zones:null);
                logs.Add("ASRock motherboard Static mint 70%; SDK mode="+confirmed.ActiveMode+"; ARGB zones="+string.Join(",",zones??[]));
                Thread.Sleep(5000);
            }
            File.WriteAllText(Output(args), JsonSerializer.Serialize(new { Admin = AppFiles.IsAdmin, Devices = devices, Logs = logs.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Output(args), JsonSerializer.Serialize(new { Error = ex.ToString(), Logs = logs.ToArray() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
        finally { if(process != null) { try { if(!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch { } process.Dispose(); } }
    }
}
