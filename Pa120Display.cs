using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using HidSharp;

namespace FanRgbStudio;

public record DisplayMetrics(float? CpuTemperature, float? CpuLoad, float? GpuTemperature, float? GpuLoad, DateTime Timestamp)
{
    public static DisplayMetrics From(Snapshot snapshot)
    {
        float? Find(string type, Func<SensorReading,bool> choose) => snapshot.Sensors.FirstOrDefault(s => s.Type == type && choose(s) && s.Value is float f && float.IsFinite(f) && (type != "Temperature" || f > 0))?.Value;
        bool Cpu(SensorReading s) => s.Id.StartsWith("/amdcpu/") || s.Id.StartsWith("/intelcpu/");
        // Prefer package temperature and the discrete GPU core; do not substitute a VRM/hotspot reading.
        var cpuTemp = Find("Temperature", s => Cpu(s) && (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))) ?? Find("Temperature", Cpu);
        var gpuPrefix = snapshot.Sensors.Any(s => s.Id.StartsWith("/gpu-nvidia/")) ? "/gpu-nvidia/" : "/gpu-amd/";
        var gpuTemp = Find("Temperature", s => s.Id.StartsWith(gpuPrefix) && (s.Name == "GPU Core" || s.Name == "GPU Temperature")) ?? Find("Temperature", s => s.Id.StartsWith(gpuPrefix));
        return new(cpuTemp, Find("Load", s => Cpu(s) && s.Name == "CPU Total"), gpuTemp, Find("Load", s => s.Id.StartsWith(gpuPrefix) && s.Name == "GPU Core"), snapshot.Timestamp);
    }
}
public record DisplayOptions(Color Color, int Brightness, bool TemperatureColor, bool CpuTemperature, bool CpuLoad, bool GpuTemperature, bool GpuLoad);

// PA120 DIGITAL only, observed in the user's TRCC. This is a numeric segment display, not an LCD framebuffer.
// Wire-format and physical index facts: docs/TRCC-USBLED.md and the linked public PA120 protocol reference.
public static class Pa120Protocol
{
    public static byte[] InitializationReport()
    {
        var report=new byte[65];report[1]=0xda;report[2]=0xdb;report[3]=0xdc;report[4]=0xdd;report[13]=1;return report;
    }
    public static bool IsPa120Reply(byte[] report,int length)=>length>=14&&report[1]==0xda&&report[2]==0xdb&&report[3]==0xdc&&report[4]==0xdd&&report[6] is >=16 and <=31&&report[13]==1;
    public static readonly int[] WireToLogical = [1,0,15,10,11,16,14,13,12,22,17,18,23,21,20,19,29,24,25,30,28,27,26,4,5,81,80,36,31,32,37,35,34,33,43,38,39,44,42,41,40,6,9,75,76,77,79,74,73,78,68,69,70,72,67,66,71,82,83,7,8,61,62,63,65,60,59,64,54,55,56,58,53,52,57,47,48,49,51,46,45,50,2,3];
    private static readonly int[] DigitMasks = [0x3f,0x06,0x5b,0x4f,0x66,0x6d,0x7d,0x07,0x7f,0x6f];
    public static bool[] Mask(DisplayMetrics metrics, DisplayOptions options)
    {
        bool[] mask = new bool[84];
        void Digits(float? raw, int start, int digits, int[] labels, bool enabled, int partial = -1)
        {
            if (!enabled) return;
            foreach (int label in labels) mask[label] = true;
            if (raw is not float value || !float.IsFinite(value))
            { for(int n=0;n<digits;n++) mask[start+n*7+6] = true; return; } // Missing data is dashes, never zero.
            int number = Math.Clamp((int)value, 0, digits == 3 ? 199 : 100);
            bool hundred = partial >= 0 && number >= 100;
            if (hundred) { mask[partial] = mask[partial+1] = true; number -= 100; }
            string text = number.ToString().PadLeft(digits, hundred ? '0' : ' ');
            for(int n=0;n<digits;n++) if (text[n] != ' ') for(int bit=0;bit<7;bit++) mask[start+n*7+bit] = (DigitMasks[text[n]-'0'] & (1<<bit)) != 0;
        }
        Digits(metrics.CpuTemperature,10,3,[0,1,4],options.CpuTemperature);
        Digits(metrics.CpuLoad,31,2,[6],options.CpuLoad,80);
        Digits(metrics.GpuTemperature,45,3,[2,3,7],options.GpuTemperature);
        Digits(metrics.GpuLoad,66,2,[9],options.GpuLoad,82);
        return mask;
    }
    public static Color TemperatureColor(float? temperature) => temperature switch { null => Color.FromRgb(100,110,125), <30 => Colors.Cyan, <50 => Colors.Lime, <70 => Colors.Yellow, <85 => Colors.Orange, _ => Colors.Red };
    public static byte[] Frame(DisplayMetrics metrics, DisplayOptions options)
    {
        var mask = Mask(metrics, options); var frame = new byte[272];
        frame[0]=0xDA;frame[1]=0xDB;frame[2]=0xDC;frame[3]=0xDD;frame[12]=2;frame[16]=252;
        double scale = Math.Clamp(options.Brightness,0,100)/100.0*.4;
        for(int wire=0;wire<84;wire++)
        {
            int logical=WireToLogical[wire]; if(!mask[logical]) continue;
            bool cpu=logical is 0 or 1 or 4 or 5 or 6 || logical is >=10 and <45 || logical is 80 or 81;
            var color=options.TemperatureColor ? TemperatureColor(cpu ? metrics.CpuTemperature : metrics.GpuTemperature) : options.Color;
            frame[20+wire*3]=(byte)(color.R*scale);frame[21+wire*3]=(byte)(color.G*scale);frame[22+wire*3]=(byte)(color.B*scale);
        }
        return frame;
    }
    public static byte[][] Reports(byte[] frame)
    {
        if(frame.Length!=272) throw new ArgumentException("PA120 frame must contain 84 RGB LEDs.");
        byte[][] reports=new byte[5][];
        for(int n=0;n<5;n++) { reports[n]=new byte[65];Array.Copy(frame,n*64,reports[n],1,Math.Min(64,frame.Length-n*64)); }
        return reports;
    }
}

public sealed class Pa120Display : IDisposable
{
    private readonly object gate = new();
    private DisplayMetrics metrics = new(null,null,null,null,DateTime.MinValue);
    private DisplayOptions options;
    private CancellationTokenSource? cancellation;
    private Task? task;
    private HidStream? stream;
    private FileStream? lease;
    public event Action<string>? Status;
    public bool Running => task is { IsCompleted:false };
    public Pa120Display(DisplayOptions options) => this.options=options;
    public static bool Connected => DeviceList.Local.GetHidDevices(0x0416,0x8001).Any(d=>d.GetMaxOutputReportLength()==65);
    public void Update(DisplayMetrics next,DisplayOptions settings) {lock(gate) {metrics=next;options=settings;} }
    public void Start()
    {
        if(Running)return;
        // TRCC and this app must not interleave their multi-report frame writes.
        if(Process.GetProcessesByName("TRCC").Length>0) throw new InvalidOperationException("TRCC가 디스플레이를 사용 중입니다. TRCC의 트레이 메뉴에서 종료한 뒤 다시 연결해 주세요.");
        var devices=DeviceList.Local.GetHidDevices(0x0416,0x8001).Where(d=>d.GetMaxOutputReportLength()==65).ToArray();
        if(devices.Length!=1)throw new IOException(devices.Length==0 ? "PA120 DIGITAL USB 장치를 찾지 못했습니다." : "동일한 USB 디스플레이가 여러 개여서 자동으로 선택하지 않습니다.");
        Directory.CreateDirectory(AppFiles.Data);
        try{lease=File.Open(System.IO.Path.Combine(AppFiles.Data,"pa120-device.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new IOException("다른 Fan Studio 연결이나 테스트가 PA120을 사용 중입니다. 기존 연결을 중지하고 다시 연결하세요.");}
        try{stream=devices[0].Open();stream.WriteTimeout=1500;stream.ReadTimeout=250;}
        catch{lease.Dispose();lease=null;throw;}
        cancellation=new();var token=cancellation.Token;
        task=Task.Run(async()=>
        {
            var activeStream=stream;
            var initReply=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader=Task.Run(()=>
            {
                try
                {
                    while(!token.IsCancellationRequested)
                    {
                        var input=new byte[65];
                        try
                        {
                            int length=activeStream.Read(input);
                            if(Pa120Protocol.IsPa120Reply(input,length))initReply.TrySetResult(input);
                            else if(length>0)AppFiles.Log("DISPLAY INPUT "+Convert.ToHexString(input.AsSpan(0,length)));
                        }
                        catch(TimeoutException){}
                    }
                }
                catch(Exception ex)when(ex is IOException or ObjectDisposedException){if(!token.IsCancellationRequested)initReply.TrySetException(ex);}
            });
            try
            {
                // The installed TRCC 2.1.8.2 initializes each connection and continuously drains input.
                activeStream.Write(Pa120Protocol.InitializationReport());
                var reply=await initReply.Task.WaitAsync(TimeSpan.FromSeconds(2),token);
                AppFiles.Log("DISPLAY INIT "+Convert.ToHexString(reply));
                Status?.Invoke($"PA120 초기 응답 확인 · PM {reply[6]}");
                await Task.Delay(50,token);
                var statusClock=Stopwatch.StartNew();bool firstFrame=true;
                while(!token.IsCancellationRequested)
                {
                    DisplayMetrics next;DisplayOptions settings;lock(gate){next=metrics;settings=options;}
                    if(DateTime.UtcNow-next.Timestamp>TimeSpan.FromSeconds(5))next=new(null,null,null,null,DateTime.UtcNow);
                    foreach(var report in Pa120Protocol.Reports(Pa120Protocol.Frame(next,settings)))activeStream.Write(report);
                    if(firstFrame||statusClock.ElapsedMilliseconds>=1000){Status?.Invoke(next.CpuTemperature.HasValue ? $"PA120 전송 중 · CPU {(int)next.CpuTemperature.Value}°C · 센서 1초마다 갱신" : "PA120 전송 중 · CPU 온도 신호 대기 (—로 표시)");firstFrame=false;statusClock.Restart();}
                    await Task.Delay(100,token);
                }
            }
            catch(OperationCanceledException){}
            catch(Exception ex){AppFiles.Log("DISPLAY "+ex);Status?.Invoke("디스플레이 전송 중단: "+ex.Message);}
            finally {cancellation?.Cancel();activeStream.Dispose();try{await reader.WaitAsync(TimeSpan.FromSeconds(1));}catch{}stream=null;lease?.Dispose();lease=null;}
        });
    }
    public void Dispose()
    {
        var stop=cancellation;stop?.Cancel();try{task?.Wait(3000);}catch{}
        if(task?.IsCompleted==false){stream?.Dispose();try{task.Wait(1500);}catch{}}
        if(task?.IsCompleted!=false){stream?.Dispose();stream=null;stop?.Dispose();}
        else if(stop!=null)_=task.ContinueWith(_=>stop.Dispose());
        cancellation=null;
    }
}
