using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FanRgbStudio;

public partial class MainWindow : Window
{
    private Process? worker, rgbEngine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, CheckBox> channels = new();
    private Snapshot? latest;
    private DateTime lastFrame = DateTime.MinValue;
    private bool closing, rgbBusy;
    private string previousChannels = "", savedEffect = "";
    private Pa120Display? coolerDisplay;
    private DateTime lastRgbWrite=DateTime.MinValue;
    private readonly TrayController tray;
    private bool exitRequested,backgroundExplained;
    private DateTime lastRuntimeStatus=DateTime.MinValue;
    private string savedRgbIdentity="",savedRgbZone="all";
    private bool rgbRefreshing;
    private DateTime lastRgbRefresh=DateTime.MinValue;
    private FanSession? resumeSession;
    private readonly bool restoreFans;
    private bool selectedRestoredFans;
    private Dictionary<string,RgbCalibration> calibrations = new();
    private RgbProfiles rgbProfiles=RgbProfiles.Empty;
    private RgbRestoreQueue rgbRestore=new(RgbProfiles.Empty,DateTime.UtcNow);
    private Preferences? legacyRgb;
    private DateTime nextLegacyAttempt=DateTime.MinValue,legacyDeadline=DateTime.UtcNow.AddMinutes(2);
    private readonly List<string> rgbRestoreEvents=[];
    private readonly System.Collections.ObjectModel.ObservableCollection<CurveEditorRow> curveRows=[];
    private readonly System.Threading.SemaphoreSlim rgbGate=new(1,1);
    private bool rgbConnectionHealthy,rgbRestoreSuspended;
    private string? lastRgbError;
    public MainWindow(bool startDisplay=false,bool startBackground=false,string? resumeFile=null,bool startRgb=false,bool restoreFans=true)
    {
        InitializeComponent();
        VersionText.Text=$"v{AppFiles.Version} · LOCAL ONLY";
        this.restoreFans=restoreFans;
        if(!restoreFans)Title="Fan Studio · UI test (no hardware writes)";
        resumeSession=FanSession.Load(resumeFile);
        var prefs = AppFiles.Load(); savedEffect = prefs.Effect;
        CustomCurveGrid.ItemsSource=curveRows;
        LoadCurveRows(FanCurve.ValidateCustom(prefs.CustomCurve,out _)?prefs.CustomCurve!:FanCurve.DefaultCustom);
        if(restoreFans)
        {
            try
            {
                if(RgbProfileStore.Default.Exists)rgbProfiles=RgbProfileStore.Default.Load();
                else if(!string.IsNullOrEmpty(prefs.RgbIdentity))legacyRgb=prefs;
                rgbRestore=new(rgbProfiles,DateTime.UtcNow);
            }
            catch(Exception ex){AppFiles.Log("RGB PROFILE LOAD "+ex.Message);RgbRestoreNotice.Text="저장된 조명을 읽지 못했어요. 조명 설정을 다시 적용해 주세요.";}
        }
        calibrations=prefs.RgbCalibrations??new();
        savedRgbIdentity=prefs.RgbIdentity;savedRgbZone=prefs.RgbZone;
        DisplayColorInput.Text=prefs.DisplayColor;DisplayBrightness.Value=Math.Clamp(prefs.DisplayBrightness,0,100);DisplayEffect.SelectedIndex=prefs.DisplayEffect=="단색" ? 1 : 0;
        ShowCpuTemp.IsChecked=prefs.CpuTemp;ShowCpuLoad.IsChecked=prefs.CpuLoad;ShowGpuTemp.IsChecked=prefs.GpuTemp;ShowGpuLoad.IsChecked=prefs.GpuLoad;
        ColorInput.Text = prefs.Color; BrightnessSlider.Value = Math.Clamp(prefs.Brightness, 0, 100);
        ManualSlider.Value = Math.Clamp(prefs.Manual, 30, 100);
        FanMode.SelectedIndex = prefs.FanMode switch { "저소음" => 0, "성능" => 2, "수동" => 3,"사용자 곡선"=>4, _ => 1 };
        foreach (string hex in new[] { "#53D9C5", "#9B89FF", "#F58BB5", "#F7B86A", "#6BAAFF", "#FFFFFF" })
        {
            var b = new Button { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 9, 0), ToolTip = hex };
            System.Windows.Automation.AutomationProperties.SetName(b, "색상 " + hex);
            b.Click += (_, _) => ColorInput.Text = hex; Swatches.Children.Add(b);
        }
        AdminButton.IsEnabled = !AppFiles.IsAdmin;
        bool driver = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        DriverText.Text = driver ? "PawnIO " + LibreHardwareMonitor.PawnIo.PawnIo.Version + " 설치됨" : "팬 제어 준비: 드라이버 미설치";
        DriverButton.IsEnabled = !driver;
        if (driver) DriverButton.Content = "센서 드라이버 설치됨";
        AccessText.Text = AppFiles.IsAdmin ? "관리자 권한" : "일반 권한 · 팬 제어에 관리자 필요";
        tray=new TrayController(ShowSettings,()=>HideToTray(true),RequestExit);
        if(startBackground){ShowActivated=false;ShowInTaskbar=false;WindowState=WindowState.Minimized;}
        Loaded += async (_, _) => { if(startBackground)HideToTray(false);StartWorker(); if(startDisplay)DisplayConnect_Click(this,new RoutedEventArgs());if(startRgb||(restoreFans&&(rgbProfiles.Devices.Count>0||legacyRgb!=null)))ConnectRgb_Click(this,new RoutedEventArgs());else await RefreshRgb(); DrawCurve(); };
        timer.Tick += (_, _) =>
        {
            Send(new("heartbeat"));
            if(restoreFans)_=RestoreRgbIfDueAsync();
            if(DateTime.UtcNow-lastRuntimeStatus>TimeSpan.FromSeconds(5))WriteRuntimeStatus();
            if(IsVisible&&!rgbBusy&&!rgbRefreshing&&RgbDevices.ItemsSource!=null&&DateTime.UtcNow-lastRgbRefresh>TimeSpan.FromSeconds(30))_=RefreshRgb();
            if(latest!=null && TryDisplayOptions(out var displayOptions))coolerDisplay?.Update(DisplayMetrics.From(latest),displayOptions);
            if (DateTime.UtcNow - lastFrame > TimeSpan.FromSeconds(7))
            {
                ApplyFanButton.IsEnabled = false;
                CpuText.Text = "— °C"; GpuText.Text = "— °C"; RpmText.Text = "— RPM";
                if (worker?.HasExited == true) FanNotice.Text = "센서 프로세스가 종료되었습니다. 진단 로그를 확인하고 앱을 다시 열어 주세요.";
                else FanNotice.Text = "센서 응답 대기 중… 실제 측정값이 오면 표시됩니다.";
            }
        };
        Closing += OnClosing;
        UpdateRgbControls();
        Closed+=(_,_)=>{tray.Dispose();WriteRuntimeStatus();};
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==WindowStateProperty&&e.NewValue is WindowState.Minimized&&tray is not null&&!closing)
            Dispatcher.BeginInvoke(()=>{if(WindowState==WindowState.Minimized&&!closing)HideToTray(false);},DispatcherPriority.Background);
    }
    public void ShowSettings()
    {
        if(closing)return;
        ShowInTaskbar=true;WindowState=WindowState.Normal;Show();Activate();WriteRuntimeStatus();if(!rgbBusy&&!rgbRefreshing)_=RefreshRgb();
    }
    public void HideToTray(bool explain)
    {
        if(closing)return;
        if(!tray.Visible){AppFiles.Log("Tray icon unavailable; refusing to hide window.");return;}
        Hide();ShowInTaskbar=false;
        if(explain&&!backgroundExplained){tray.ExplainBackground();backgroundExplained=true;}
        WriteRuntimeStatus();
    }
    public void RequestExit(){exitRequested=true;Close();}
    public void AllowSessionExit()=>exitRequested=true;
    private void Background_Click(object sender,RoutedEventArgs e)=>HideToTray(true);
    internal bool TrayVisible=>tray.Visible;
    internal bool IsStopping=>closing;
    internal DateTime LastSensorFrame=>lastFrame;
    internal int? WorkerProcessId=>worker is{HasExited:false}?worker.Id:null;
    internal bool RgbWorkPending=>rgbBusy||rgbRefreshing;
    private void WriteRuntimeStatus()
    {
        if(!restoreFans)return;
        try
        {
            Directory.CreateDirectory(AppFiles.Data);
            var path=System.IO.Path.Combine(AppFiles.Data,"runtime-status.json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new{Pid=Environment.ProcessId,Version=AppFiles.Version,Timestamp=DateTime.UtcNow,Visible=IsVisible,ShowInTaskbar,TrayVisible=tray.Visible,WorkerPid=WorkerProcessId,SensorTimestamp=latest?.Timestamp,DisplayRunning=coolerDisplay?.Running==true,Stopping=closing,RgbEnginePid=rgbEngine is{HasExited:false}?rgbEngine.Id:(int?)null,RgbDevices=(RgbDevices.ItemsSource as List<RgbDevice>)?.Select(d=>d.Name).ToArray(),CaseFanHeadersDetected=(RgbDevices.ItemsSource as List<RgbDevice>)?.Any(d=>d.HasCaseFanHeaders)==true,SelectedRgbZone=(RgbZones.SelectedItem as RgbZoneChoice)?.Key,RgbRestorePending=rgbRestore.PendingCount,RgbRestoreCompleted=rgbRestore.CompletedCount,RgbRestoreStatus=rgbRestore.Status,RgbRestoreSuspended=rgbRestoreSuspended,RgbConnected=rgbConnectionHealthy,RgbLastError=lastRgbError,RgbRestoreEvents=rgbRestoreEvents.ToArray()},new JsonSerializerOptions{WriteIndented=true}));
            File.Move(path+".tmp",path,true);lastRuntimeStatus=DateTime.UtcNow;
        }
        catch(Exception ex){AppFiles.Log("RUNTIME STATUS "+ex.Message);}
    }
    private void StartWorker()
    {
        worker = new Process { StartInfo = new(Environment.ProcessPath!, "--worker"+(restoreFans?"":" --no-fan-restore")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        worker.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || closing) return;
            try { var snapshot = JsonSerializer.Deserialize<Snapshot>(e.Data); if (snapshot != null) Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot)); }
            catch (Exception ex) { AppFiles.Log("READ " + ex.Message); }
        };
        worker.ErrorDataReceived += (_, e) => { if (e.Data != null) AppFiles.Log("SENSOR " + e.Data); };
        worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine(); timer.Start();
    }
    private void Send(WorkerCommand command)
    {
        try { if (worker is { HasExited: false }) { worker.StandardInput.WriteLine(JsonSerializer.Serialize(command)); worker.StandardInput.Flush(); } } catch (Exception ex) { AppFiles.Log("IPC " + ex.Message); }
    }
    private void UpdateSnapshot(Snapshot snapshot)
    {
        if (closing) return;
        latest = snapshot; lastFrame = DateTime.UtcNow; BoardText.Text = snapshot.Board;
        var displayMetrics=DisplayMetrics.From(snapshot);
        DisplayCpuTemp.Text=displayMetrics.CpuTemperature is float ct ? $"{(int)ct} °C" : "— °C";DisplayGpuTemp.Text=displayMetrics.GpuTemperature is float gt ? $"{(int)gt} °C" : "— °C";
        DisplayCpuLoad.Text=displayMetrics.CpuLoad is float cl ? $"{(int)cl} %" : "— %";DisplayGpuLoad.Text=displayMetrics.GpuLoad is float gl ? $"{(int)gl} %" : "— %";
        var cpu = snapshot.Sensors.Where(s => s.Type == "Temperature" && s.Value is > 0 and < 140 && (s.Id.StartsWith("/amdcpu/") || s.Id.StartsWith("/intelcpu/"))).OrderByDescending(s => s.Value).FirstOrDefault();
        var gpu = snapshot.Sensors.Where(s => s.Type == "Temperature" && s.Value is > 0 and < 140 && s.Id.StartsWith("/gpu-")).OrderByDescending(s => s.Value).FirstOrDefault();
        CpuText.Text = cpu?.Value is float c ? $"{c:F0} °C" : "— °C"; GpuText.Text = gpu?.Value is float g ? $"{g:F0} °C" : "— °C";
        CpuDetail.Text = cpu?.Name ?? "관리자 권한·드라이버 확인 필요";
        var fans = snapshot.Sensors.Where(s => s.Type == "Fan" && s.Value is >= 0).ToArray();
        RpmText.Text = fans.Length > 0 ? $"{fans.Max(f => f.Value):F0} RPM" : "— RPM";
        RpmDetail.Text = fans.Length > 0 ? $"{fans.Length}개 센서 중 최고 회전수" : "회전수 센서 미감지";
        RpmDetail.ToolTip = string.Join("\n", fans.Select(f => $"{f.Hardware} / {f.Name}: {f.Value:F0} RPM"));
        string signature = string.Join("|", snapshot.Controls.Select(c => c.Id));
        if (signature != previousChannels || ChannelList.Children.OfType<CheckBox>().Count() != snapshot.Controls.Count)
        {
            var selected = channels.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToHashSet();
            channels.Clear(); ChannelList.Children.Clear(); previousChannels = signature;
            foreach (var channel in snapshot.Controls)
            {
                var check = new CheckBox { Content = channel.Name, Tag = channel.Id, Foreground = Brushes.White, Margin = new(0, 0, 0, 9), IsChecked = selected.Contains(channel.Id), ToolTip = channel.Hardware + "\n" + channel.Id };
                channels[channel.Id] = check; ChannelList.Children.Add(check);
            }
        }
        if (snapshot.Controls.Count == 0)
        {
            ChannelList.Children.Clear(); ChannelList.Children.Add(new TextBlock { Text = "조절 가능한 팬이 감지되지 않았어요.\n관리자로 다시 열기를 먼저 눌러 주세요.", Foreground = new SolidColorBrush(Color.FromRgb(147, 156, 173)), TextWrapping = TextWrapping.Wrap });
        }
        foreach (var channel in snapshot.Controls)
            if (channels.TryGetValue(channel.Id, out var check)) check.Content = $"{channel.Name} · {channel.Mode}" + (channel.Value is float v ? $" · {v:F1}%" : "");
        if(!selectedRestoredFans && snapshot.ActiveFans is { Count: > 0 })
        {
            foreach(var fan in snapshot.ActiveFans)if(channels.TryGetValue(fan.Id,out var check))check.IsChecked=true;
            if(snapshot.ActiveFans.Select(f=>f.Mode).Distinct().Count()==1)
            {
                FanMode.SelectedIndex=snapshot.ActiveFans[0].Mode switch{"저소음"=>0,"성능"=>2,"수동"=>3,"사용자 곡선"=>4,_=>1};
                if(snapshot.ActiveFans[0] is{Mode:"사용자 곡선",Curve:{} restoredCurve}&&snapshot.ActiveFans.All(f=>f.Curve!=null&&f.Curve.SequenceEqual(restoredCurve)))
                    LoadCurveRows(restoredCurve);
            }
            if(snapshot.ActiveFans.All(f=>f.Mode=="수동")&&snapshot.ActiveFans.Select(f=>f.Value).Distinct().Count()==1)
                ManualSlider.Value=snapshot.ActiveFans[0].Value;
            selectedRestoredFans=true;
        }
        if(resumeSession!=null&&snapshot.Admin&&HardwareWorker.SafetyTemperature(snapshot)!=null)
        {
            try
            {
                foreach(var command in resumeSession.Commands(snapshot)){Send(command);if(channels.TryGetValue(command.Id,out var checkbox))checkbox.IsChecked=true;}
                StatusText.Text="업데이트 전 사용 중이던 팬 설정을 복원했습니다.";AppFiles.Log("FAN SESSION restored "+resumeSession.Fans.Count+" channels");
            }
            catch(Exception ex){AppFiles.Log("FAN SESSION rejected "+ex.Message);StatusText.Text=ex.Message;}
            finally{resumeSession=null;}
        }
        ApplyFanButton.IsEnabled = snapshot.Admin && snapshot.Controls.Count > 0 && cpu != null;
        FanNotice.Text = !LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled ? "첫 사용: 왼쪽 ‘센서 드라이버 설치’ 후 ‘관리자로 다시 열기’를 눌러 주세요." : !snapshot.Admin ? "팬 제어에는 관리자 권한이 필요합니다. 왼쪽 ‘관리자로 다시 열기’를 사용하세요." : cpu == null ? "CPU 온도 미감지: 팬 제어를 잠갔습니다. PawnIO 설치 여부를 설명서에서 확인하세요." : snapshot.Notice;
        if(restoreFans)try { Directory.CreateDirectory(AppFiles.Data); File.WriteAllText(System.IO.Path.Combine(AppFiles.Data, "latest-diagnostics.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
    private string SelectedFanMode => (FanMode?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "균형";
    private void FanMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if(ManualSlider==null)return;
        ManualSlider.IsEnabled=SelectedFanMode=="수동";if(ManualEditor!=null)ManualEditor.IsEnabled=ManualSlider.IsEnabled;
        if(CustomCurvePanel!=null)CustomCurvePanel.Visibility=SelectedFanMode=="사용자 곡선"?Visibility.Visible:Visibility.Collapsed;
        if(ManualControls!=null)ManualControls.Visibility=SelectedFanMode=="사용자 곡선"?Visibility.Collapsed:Visibility.Visible;
        DrawCurve();
    }
    private void LoadCurveRows(IReadOnlyList<FanCurvePoint> points)
    {
        foreach(var row in curveRows)row.PropertyChanged-=CurveRow_Changed;
        curveRows.Clear();
        foreach(var point in points){var row=new CurveEditorRow(point);row.PropertyChanged+=CurveRow_Changed;curveRows.Add(row);}
        DrawCurve();
    }
    private void CurveRow_Changed(object? sender,PropertyChangedEventArgs e)=>DrawCurve();
    private bool TryEditorCurve(out List<FanCurvePoint> points,bool showError=true)
    {
        points=[];string error="";
        foreach(var row in curveRows)
        {
            if(!row.TryPoint(out var point)){error="온도와 출력을 숫자로 입력해 주세요.";break;}
            points.Add(point);
        }
        if(error.Length==0)FanCurve.ValidateCustom(points,out error);
        if(showError&&CurveValidationText!=null)
        {
            CurveValidationText.Text=error.Length==0?$"{points.Count}개 지점 · CPU·GPU 최고 온도 기준 · 팬 설정 적용으로 저장":error;
            CurveValidationText.Foreground=error.Length==0?new SolidColorBrush(Color.FromRgb(169,185,204)):new SolidColorBrush(Color.FromRgb(255,150,140));
        }
        return error.Length==0;
    }
    private void CommitCurveEdits(){CustomCurveGrid.CommitEdit(DataGridEditingUnit.Cell,true);CustomCurveGrid.CommitEdit(DataGridEditingUnit.Row,true);}
    private void AddCurvePoint_Click(object sender,RoutedEventArgs e)
    {
        CommitCurveEdits();if(!TryEditorCurve(out var points))return;
        if(points.Count>=12){CurveValidationText.Text="온도 지점은 최대 12개까지 설정할 수 있습니다.";return;}
        int index=Enumerable.Range(0,points.Count-1).OrderByDescending(i=>points[i+1].Temperature-points[i].Temperature).First();
        var a=points[index];var b=points[index+1];
        if(b.Temperature-a.Temperature<0.3f){CurveValidationText.Text="새 지점을 넣을 온도 간격이 부족합니다.";return;}
        points.Insert(index+1,new((float)Math.Round((a.Temperature+b.Temperature)/2,1),(float)Math.Round((a.Duty+b.Duty)/2,1)));
        LoadCurveRows(points);CustomCurveGrid.SelectedIndex=index+1;
    }
    private void RemoveCurvePoint_Click(object sender,RoutedEventArgs e)
    {
        CommitCurveEdits();if(!TryEditorCurve(out var points))return;int index=CustomCurveGrid.SelectedIndex;
        if(points.Count<=2||index<0||index==points.Count-1){CurveValidationText.Text="삭제할 지점을 선택하세요. 최소 2개 지점과 마지막 보호 지점은 유지합니다.";return;}
        points.RemoveAt(index);LoadCurveRows(points);
    }
    private void ResetCurve_Click(object sender,RoutedEventArgs e)=>LoadCurveRows(FanCurve.DefaultCustom);
    private void Manual_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ManualLabel != null) ManualLabel.Text = $"{e.NewValue:F1}%";
        if(ManualValueInput!=null)ManualValueInput.Text=e.NewValue.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
        DrawCurve();
    }
    private bool CommitManualInput()
    {
        if(!ManualDuty.TryParse(ManualValueInput.Text,out var value)){FanNotice.Text="팬 출력은 30.0~100.0 사이 숫자로 입력해 주세요.";return false;}
        ManualSlider.Value=value;ManualValueInput.Text=value.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);return true;
    }
    private void ManualInput_LostFocus(object sender,RoutedEventArgs e){if(SelectedFanMode=="수동")CommitManualInput();}
    private void ManualInput_KeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key==System.Windows.Input.Key.Enter){CommitManualInput();e.Handled=true;}
        else if(e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
        {if(CommitManualInput())ManualSlider.Value=ManualDuty.Step(ManualSlider.Value,e.Key==System.Windows.Input.Key.Up?0.1:-0.1);e.Handled=true;}
    }
    private void ManualStep_Click(object sender,RoutedEventArgs e)
    {
        if(!CommitManualInput())return;
        if(sender is Button button&&double.TryParse(button.Tag?.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var step))
            ManualSlider.Value=ManualDuty.Step(ManualSlider.Value,step);
    }
    private void Curve_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCurve();
    private void DrawCurve()
    {
        if (CurveCanvas == null || ManualSlider == null) return;
        CurveCanvas.Children.Clear(); double w = Math.Max(10, CurveCanvas.ActualWidth), h = CurveCanvas.Height;
        foreach (int level in new[] { 30, 60, 100 }) CurveCanvas.Children.Add(new Line { X1 = 0, Y1 = h - level / 100.0 * (h - 12), X2 = w, Y2 = h - level / 100.0 * (h - 12), Stroke = new SolidColorBrush(Color.FromRgb(41, 51, 66)), StrokeDashArray = [3, 4] });
        var line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(83, 217, 197)), StrokeThickness = 2.5 };
        List<FanCurvePoint>? custom=null;
        if(SelectedFanMode=="사용자 곡선")
        {
            if(!TryEditorCurve(out var points)){CurveCanvas.Children.Add(new TextBlock{Text="곡선 입력값을 확인해 주세요.",Foreground=Brushes.Salmon,Margin=new(8)});return;}
            custom=points;
        }
        double X(float t)=>8+(t-20)/65.0*(w-16);
        double Y(float duty)=>h-duty/100.0*(h-12);
        for (int temp = 20; temp <= 85; temp++) line.Points.Add(new(X(temp),Y(FanCurve.Evaluate(SelectedFanMode,(float)ManualSlider.Value,temp,custom:custom))));
        CurveCanvas.Children.Add(line);
        if(custom!=null)foreach(var point in custom){var marker=new Ellipse{Width=6,Height=6,Fill=line.Stroke};Canvas.SetLeft(marker,X(point.Temperature)-3);Canvas.SetTop(marker,Y(point.Duty)-3);CurveCanvas.Children.Add(marker);}
    }
    private void ApplyFan_Click(object sender, RoutedEventArgs e)
    {
        if(SelectedFanMode=="수동"&&!CommitManualInput())return;
        List<FanCurvePoint>? custom=null;
        if(SelectedFanMode=="사용자 곡선"){CommitCurveEdits();if(!TryEditorCurve(out custom)){FanNotice.Text="곡선 입력값을 먼저 확인해 주세요.";return;}}
        var selected = channels.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToArray();
        if (selected.Length == 0) { FanNotice.Text = "먼저 조절할 팬 채널을 체크해 주세요. 펌프 채널은 선택하지 마세요."; return; }
        foreach (var id in selected) Send(new("set", id, SelectedFanMode, (float)ManualSlider.Value,custom));
        FanNotice.Text = "팬 설정을 전달했습니다. 다음 센서 응답에서 결과를 확인합니다.";
    }
    private void Restore_Click(object sender, RoutedEventArgs e) { Send(new("restore")); FanNotice.Text = "이 앱이 제어하던 팬을 기본 제어로 돌리는 중입니다…"; }
    private async Task RefreshRgb()
    {
        if(rgbRefreshing||closing)return;rgbRefreshing=true;UpdateRgbControls();
        string identity=(RgbDevices.SelectedItem as RgbDevice)?.Identity??savedRgbIdentity;
        var pendingCalibration=RgbDevices.SelectedItem is RgbDevice?CurrentCalibration:null;
        string effect=(RgbEffects.SelectedItem as RgbMode)?.Name??savedEffect;
        int? pendingSpeed=(RgbEffects.SelectedItem as RgbMode)?.SupportsSpeed==true?(int)RgbSpeedSlider.Value:null;
        string zone=(RgbZones.SelectedItem as RgbZoneChoice)?.Key??(string.IsNullOrEmpty(savedRgbIdentity)?"":savedRgbZone);
        try
        {
            var devices = await RgbAsync(() => { using var client = new OpenRgbClient(); return client.Devices(); });
            rgbConnectionHealthy=true;lastRgbError=null;
            RgbDevices.ItemsSource = devices;
            RgbDevices.SelectedItem=devices.FirstOrDefault(d=>d.Identity==identity)??devices.FirstOrDefault(d=>d.HasCaseFanHeaders)??devices.FirstOrDefault();
            if(RgbDevices.SelectedItem is RgbDevice selected)
            {
                if(selected.Identity==identity&&pendingCalibration!=null){RedGain.Value=pendingCalibration.Red;GreenGain.Value=pendingCalibration.Green;BlueGain.Value=pendingCalibration.Blue;}
                if(selected.Modes.FirstOrDefault(m=>m.Name==effect) is {} mode)RgbEffects.SelectedItem=mode;
                if(selected.Identity==identity&&pendingSpeed!=null&&(RgbEffects.SelectedItem as RgbMode)?.Name==effect)RgbSpeedSlider.Value=pendingSpeed.Value;
                if(RgbZones.ItemsSource is List<RgbZoneChoice> choices&&choices.FirstOrDefault(c=>c.Key==zone) is {} choice)RgbZones.SelectedItem=choice;
            }
            CaseFanButton.IsEnabled=devices.Any(d=>d.HasCaseFanHeaders);
            RgbNotice.Text=devices.Count==0?"RGB 엔진은 연결됐지만 지원 장치가 감지되지 않았어요. 다른 RGB 프로그램과 장치 지원 여부를 확인해 주세요.":$"{devices.Count}개 장치 연결됨 · 장치와 효과를 선택하세요.";
            ConnectRgbButton.Content = "다시 검색";
        }
        catch (Exception ex) { rgbConnectionHealthy=false;lastRgbError=ex.Message;RgbNotice.Text = "RGB 연결 실패: "+ex.Message+" · RGB 복구로 다시 연결할 수 있습니다."; AppFiles.Log("RGB " + ex.Message); }
        finally{rgbRefreshing=false;lastRgbRefresh=DateTime.UtcNow;UpdateRgbControls();}
    }
    private async Task<T> RgbAsync<T>(Func<T> operation)
    {await rgbGate.WaitAsync();try{return await Task.Run(operation);}finally{rgbGate.Release();}}
    private async void ConnectRgb_Click(object sender, RoutedEventArgs e)
    {
        if (rgbBusy||rgbRefreshing) return; rgbBusy = true; UpdateRgbControls();
        try
        {
            bool connected = await RgbAsync(() => { try { using var c = new OpenRgbClient(); c.Devices(); return true; } catch { return false; } });
            if (!connected)
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "OpenRGB", "OpenRGB.exe");
                if (!File.Exists(path)) throw new FileNotFoundException("OpenRGB 엔진이 없습니다. 배포 폴더 전체를 함께 옮겨 주세요.", path);
                if (rgbEngine is { HasExited: false }) { RgbNotice.Text = "RGB 엔진이 응답하지 않아요. ‘RGB 복구’로 이 앱의 엔진만 다시 시작할 수 있습니다.";rgbConnectionHealthy=false; return; }
                if(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==6742))throw new IOException("다른 RGB 엔진이 응답하지 않습니다. 해당 프로그램에서 엔진을 다시 시작한 뒤 연결해 주세요.");
                Directory.CreateDirectory(System.IO.Path.Combine(AppFiles.Data, "OpenRGB"));
                var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path)!, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "--server", "--server-host", "127.0.0.1", "--server-port", "6742", "--noautoconnect", "--config", System.IO.Path.Combine(AppFiles.Data, "OpenRGB") }) start.ArgumentList.Add(arg);
                var engine = new Process { StartInfo = start };
                engine.OutputDataReceived += (_, data) => { if (data.Data != null) AppFiles.Log("OPENRGB " + data.Data); };
                engine.ErrorDataReceived += (_, data) => { if (data.Data != null) AppFiles.Log("OPENRGB " + data.Data); };
                try{engine.Start();}catch{engine.Dispose();throw;}
                rgbEngine?.Dispose();rgbEngine=engine;engine.BeginOutputReadLine();engine.BeginErrorReadLine();
                RgbNotice.Text = "RGB 엔진을 시작하고 장치를 검색하고 있어요…";
                var startupWatch=Stopwatch.StartNew();bool engineReady=false;
                for (int i = 0; i < 30 && !closing && startupWatch.Elapsed<TimeSpan.FromSeconds(30); i++)
                {
                    await Task.Delay(1000);
                    bool ready = await RgbAsync(() => { try { using var c = new OpenRgbClient(operationBudget:TimeSpan.FromSeconds(1)); return true; } catch { return false; } });
                    if (ready){engineReady=true;break;}
                    if(rgbEngine.HasExited)throw new IOException("RGB 엔진이 종료됐습니다. 다른 OpenRGB 실행 여부와 진단 로그를 확인해 주세요.");
                }
                if(!engineReady&&!closing)throw new TimeoutException("RGB 엔진이 시작 시간 안에 응답하지 않았어요. RGB 복구 또는 진단 저장을 사용해 주세요.");
            }
            if (!closing) await RefreshRgb();
        }
        catch (Exception ex) { rgbConnectionHealthy=false;lastRgbError=ex.Message;RgbNotice.Text = ex.Message; AppFiles.Log(ex.ToString()); }
        finally
        {
            rgbBusy = false;
            if (!closing)UpdateRgbControls();
        }
    }
    private async void RecoverRgb_Click(object sender,RoutedEventArgs e)
    {
        if(rgbBusy||rgbRefreshing)return;
        rgbBusy=true;rgbRestoreSuspended=true;legacyRgb=null;rgbRestore=new(RgbProfiles.Empty,DateTime.UtcNow);UpdateRgbControls();
        bool external=rgbEngine is not{HasExited:false}&&System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==6742);
        try
        {
            if(!external&&rgbEngine is{HasExited:false} owned)
            {
                await RgbAsync(()=>{if(!owned.HasExited){owned.Kill();if(!owned.WaitForExit(4000))throw new IOException("RGB 엔진 종료 대기 시간이 초과됐습니다.");}return true;});
                owned.Dispose();rgbEngine=null;
            }
        }
        catch(Exception ex){lastRgbError=ex.Message;RgbNotice.Text="RGB 복구 실패: "+ex.Message;return;}
        finally{rgbBusy=false;UpdateRgbControls();}
        if(external)
        {
            await RefreshRgb();RgbNotice.Text="외부 RGB 엔진은 강제 종료하지 않았어요. 엔진을 실행한 프로그램에서 다시 시작하거나 효과를 다시 적용해 주세요.";return;
        }
        RgbNotice.Text="RGB 엔진만 다시 연결합니다. 팬 속도·쿨러 숫자 전송은 유지합니다.";
        ConnectRgb_Click(sender,e);
    }
    private void RestoreRgb_Click(object sender,RoutedEventArgs e)
    {
        if(rgbBusy||rgbRefreshing)return;
        try{rgbProfiles=RgbProfileStore.Default.Load();rgbRestore=new(rgbProfiles,DateTime.UtcNow);rgbRestoreSuspended=false;RgbRestoreNotice.Text="저장된 조명 재적용을 요청했어요.";ConnectRgb_Click(sender,e);}
        catch(Exception ex){lastRgbError=ex.Message;RgbRestoreNotice.Text="저장 설정 읽기 실패: "+ex.Message;}
    }
    private async void ExportRgb_Click(object sender,RoutedEventArgs e)
    {
        if(rgbBusy||rgbRefreshing)return;rgbBusy=true;UpdateRgbControls();
        try
        {
            var devices=(RgbDevices.ItemsSource as List<RgbDevice>)??[];
            try{devices=await RgbAsync(()=>{using var client=new OpenRgbClient();return client.Devices();});}
            catch(Exception ex){lastRgbError=ex.Message;}
            var path=System.IO.Path.Combine(AppFiles.Data,"rgb-diagnostics.json");
            RgbDiagnostics.Save(path,devices,rgbEngine is{HasExited:false}?"app-owned":"external or disconnected",lastRgbError);
            RgbNotice.Text="RGB 진단을 저장했어요. 왼쪽 ‘진단 폴더 열기’의 rgb-diagnostics.json을 보내 주세요. 사용자 이름·USB 경로·시리얼 번호는 포함하지 않습니다.";
        }
        catch(Exception ex){RgbNotice.Text="진단 저장 실패: "+ex.Message;}
        finally{rgbBusy=false;UpdateRgbControls();}
    }
    private void RememberRgb(RgbDevice device,SavedRgbSetting setting)
    {
        var updated=rgbProfiles.Upsert(device,setting);RgbProfileStore.Default.Save(updated);rgbProfiles=updated;
        rgbRestore.Cancel(device.Identity);if(legacyRgb?.RgbIdentity==device.Identity)legacyRgb=null;
        RgbRestoreNotice.Text="장치별 조명 설정 저장됨 · 다음 로그인 후 자동 복원";
    }
    private async Task RestoreRgbIfDueAsync()
    {
        if(closing||rgbBusy||rgbRefreshing||rgbRestoreSuspended)return;
        var now=DateTime.UtcNow;
        if(legacyRgb!=null&&now>=legacyDeadline){AppFiles.Log("RGB legacy migration timed out");legacyRgb=null;RgbRestoreNotice.Text="저장된 조명의 장치 감지 시간 초과 · RGB 연결로 다시 시도하세요.";if(rgbRestore.PendingCount==0)return;}
        var request=rgbRestore.Due(now);
        if(request==null&&(legacyRgb==null||now<nextLegacyAttempt)){if(legacyRgb==null)RgbRestoreNotice.Text=rgbRestore.Status;return;}
        rgbBusy=true;UpdateRgbControls();
        try
        {
            if(legacyRgb is {} legacy)
            {
                var migrated=await RgbAsync(()=>
                {
                    using var client=new OpenRgbClient();var device=client.Devices().SingleOrDefault(d=>d.Identity==legacy.RgbIdentity)??throw new IOException("저장된 RGB 장치 감지 대기 중");
                    return (Device:device,Setting:SavedRgbSetting.Capture(device,legacy.RgbZone,legacy.Effect,legacy.Color,legacy.Brightness,legacy.RgbCalibrations?.GetValueOrDefault(device.Identity)??new()));
                });
                if(closing)return;
                var migratedProfile=rgbProfiles.Upsert(migrated.Device,migrated.Setting);RgbProfileStore.Default.Save(migratedProfile);rgbProfiles=migratedProfile;
                legacyRgb=null;rgbRestore=new(rgbProfiles,DateTime.UtcNow);request=rgbRestore.Due(DateTime.UtcNow);
                AppFiles.Log("RGB legacy settings migrated: "+migrated.Device.Name);
            }
            if(request==null)return;
            var target=request;lastRgbWrite=DateTime.UtcNow;
            await RgbAsync(()=>{using var client=new OpenRgbClient();target.Apply(client);return true;});
            if(closing)return;
            await Task.Delay(1500);
            rgbRestore.Succeeded(target.Identity,DateTime.UtcNow);
            var result=$"{DateTime.UtcNow:O} {target.Name} "+string.Join("; ",target.Settings.Select(s=>$"{s.Zone} {s.Mode} {s.Color} {s.Brightness}%"));
            rgbRestoreEvents.Add(result);AppFiles.Log("RGB AUTO RESTORE "+result);
        }
        catch(Exception ex)
        {
            lastRgbError=ex.Message;
            if(request!=null)rgbRestore.Failed(request.Identity,DateTime.UtcNow,ex.Message,ex is not InvalidDataException);
            else nextLegacyAttempt=DateTime.UtcNow.AddSeconds(5);
            AppFiles.Log("RGB AUTO RESTORE retry "+ex.Message);
        }
        finally
        {
            rgbBusy=false;
            if(!closing){UpdateRgbControls();RgbRestoreNotice.Text=legacyRgb!=null?"기존 조명 설정의 장치 감지 대기 중 · 자동 재시도":rgbRestore.Status;WriteRuntimeStatus();}
        }
    }
    private void RgbDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RgbEffects == null) return;
        var device = RgbDevices.SelectedItem as RgbDevice; RgbEffects.ItemsSource = device?.Modes;
        if(RedGain!=null){var correction=device!=null&&calibrations.TryGetValue(device.Identity,out var saved)?saved:new();RedGain.Value=correction.Red;GreenGain.Value=correction.Green;BlueGain.Value=correction.Blue;}
        if(RgbZones!=null){RgbZones.ItemsSource=device!=null?RgbZoneChoice.For(device):null;RgbZones.SelectedIndex=device?.HasCaseFanHeaders==true?1:0;}
        RgbEffects.SelectedItem = device?.Modes.FirstOrDefault(m => m.Name == savedEffect && m.Name!="Off") ?? PreferredSolid(device) ?? device?.Modes.FirstOrDefault(m=>m.Name!="Off");
        if(device!=null&&!rgbRefreshing&&rgbProfiles.Devices.FirstOrDefault(d=>d.Identity==device.Identity)?.Settings.LastOrDefault() is {} stored)
        {
            ColorInput.Text=stored.Color;BrightnessSlider.Value=stored.Brightness;
            if(device.Modes.FirstOrDefault(m=>m.Name==stored.Mode) is{} restoredMode)RgbEffects.SelectedItem=restoredMode;
            if(RgbZones?.ItemsSource is List<RgbZoneChoice> savedZones)RgbZones.SelectedItem=savedZones.FirstOrDefault(z=>z.Key==stored.Zone)??savedZones[0];
            if(stored.SpeedPercent!=null)RgbSpeedSlider.Value=stored.SpeedPercent.Value;
        }
        UpdateRgbControls();
    }
    private void CaseFan_Click(object sender,RoutedEventArgs e)
    {
        if(RgbDevices.ItemsSource is not List<RgbDevice> devices)return;
        var board=devices.FirstOrDefault(d=>d.HasCaseFanHeaders);if(board==null){RgbNotice.Text="메인보드 RGB USB 장치가 감지되지 않았어요. 다시 검색해 주세요.";return;}
        RgbDevices.SelectedItem=board;RgbEffects.SelectedItem=PreferredSolid(board);
        if(RgbZones.ItemsSource is List<RgbZoneChoice> choices)RgbZones.SelectedItem=choices.FirstOrDefault(c=>c.Key=="argb");
        RgbNotice.Text="시스템 팬용 ARGB 헤더를 선택했어요. 색상·밝기를 고르고 ‘조명 설정 적용’을 누르세요.";
    }
    private static RgbMode? PreferredSolid(RgbDevice? device)=>device?.Modes.FirstOrDefault(m=>m.Name.Equals("Static",StringComparison.OrdinalIgnoreCase)&&m.SupportsColor)??device?.Modes.FirstOrDefault(m=>m.Name.Equals("Direct",StringComparison.OrdinalIgnoreCase)&&m.SupportsColor);
    private void RgbEffect_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(RgbZones==null||RgbSpeedSlider==null)return;
        var mode=RgbEffects.SelectedItem as RgbMode;bool perLed=mode?.SupportsZoneColor==true;
        if(!perLed&&RgbZones.ItemsSource is List<RgbZoneChoice> choices)RgbZones.SelectedItem=choices.First(c=>c.Key=="all");
        if(mode!=null&&mode.SupportsSpeed)RgbSpeedSlider.Value=mode.DefaultSpeedPercent((RgbDevices.SelectedItem as RgbDevice)?.ActiveMode==mode.Index);
        UpdateRgbControls();
    }
    private void RgbSpeed_Changed(object sender,RoutedPropertyChangedEventArgs<double> e){if(RgbSpeedLabel!=null)RgbSpeedLabel.Text=$"효과 속도 {e.NewValue:F0}%";}
    private int? CurrentRgbSpeed=>(RgbEffects.SelectedItem as RgbMode)?.SupportsSpeed==true?(int)RgbSpeedSlider.Value:null;
    private void RestartRgbEffect_Click(object sender,RoutedEventArgs e)
    {
        if(RgbEffects.SelectedItem is not RgbMode mode||mode.IsSolid||mode.Name.Equals("Off",StringComparison.OrdinalIgnoreCase)){RgbNotice.Text="움직이는 효과를 선택한 뒤 다시 시작해 주세요.";return;}
        if(mode.SupportsSpeed)RgbSpeedSlider.Value=50;ApplyRgb_Click(sender,e);
    }
    private void UpdateRgbControls()
    {
        if(RgbCapabilityText==null||RgbCalibrationPanel==null||SaveSettingsButton==null)return;
        var mode=RgbEffects.SelectedItem as RgbMode;var device=RgbDevices.SelectedItem as RgbDevice;
        bool idle=!rgbBusy&&!rgbRefreshing&&!closing,connected=idle&&rgbConnectionHealthy;
        ApplyRgbButton.IsEnabled=connected&&device!=null&&mode!=null;ConnectRgbButton.IsEnabled=idle;
        RgbDevices.IsEnabled=idle;RgbEffects.IsEnabled=idle;RgbZones.IsEnabled=connected&&mode?.SupportsZoneColor==true;
        RecoverRgbButton.IsEnabled=ExportRgbButton.IsEnabled=idle;RestoreRgbButton.IsEnabled=idle&&rgbProfiles.Devices.Count>0;
        CaseFanButton.IsEnabled=connected&&(RgbDevices.ItemsSource as List<RgbDevice>)?.Any(d=>d.HasCaseFanHeaders)==true;
        SolidRgbButton.IsEnabled=connected&&PreferredSolid(device)!=null;
        RestartRgbEffectButton.IsEnabled=connected&&mode!=null&&!mode.IsSolid&&!mode.Name.Equals("Off",StringComparison.OrdinalIgnoreCase);
        RgbColorEditor.IsEnabled=RgbCalibrationPanel.IsEnabled=connected&&mode?.SupportsColor==true;
        BrightnessSlider.IsEnabled=connected&&mode?.SupportsBrightness==true;
        RgbSpeedPanel.Visibility=mode?.SupportsSpeed==true?Visibility.Visible:Visibility.Collapsed;RgbSpeedSlider.IsEnabled=connected;
        SaveSettingsButton.IsEnabled=idle;
        RgbCapabilityText.Text=mode==null?"장치와 효과를 선택하세요.":mode.IsSolid?"단색 모드는 움직임 없이 선택한 색으로 고정합니다.":!mode.SupportsColor?"이 효과는 자동 색상을 사용해 HEX 색상을 바꿀 수 없어요. 색을 지정하려면 ‘단색으로 켜기’를 선택하세요.":"이 효과는 선택한 색상을 사용할 수 있습니다.";
        if(mode!=null&&!mode.SupportsBrightness)RgbCapabilityText.Text+=" 밝기 조절은 지원하지 않습니다.";
        if(mode!=null&&(mode.Flags&0x40)!=0&&(mode.Field(11)==2||(mode.Flags&0x20)==0)&&Math.Max(mode.Colors,mode.Field(6))>1)
            RgbCapabilityText.Text+=" 다중 색상 효과는 첫 번째 색만 바꾸고 나머지 팔레트를 유지합니다.";
        if(mode!=null&&!mode.SupportsZoneColor)RgbCapabilityText.Text+=" 효과는 장치 전체에 적용됩니다.";
        UpdatePreview();
    }
    internal void PreviewRgbForTest(RgbDevice fixture)
    {
        if(restoreFans)throw new InvalidOperationException("RGB UI fixtures are only allowed in the read-only smoke test.");
        rgbConnectionHealthy=true;RgbDevices.ItemsSource=new List<RgbDevice>{fixture};RgbDevices.SelectedItem=fixture;UpdateRgbControls();
    }
    private void SolidRgb_Click(object sender,RoutedEventArgs e)
    {
        var mode=PreferredSolid(RgbDevices.SelectedItem as RgbDevice);if(mode==null)return;RgbEffects.SelectedItem=mode;if(BrightnessSlider.Value==0)BrightnessSlider.Value=70;ApplyRgb_Click(sender,e);
    }
    private bool TryColor(out Color color)
    {
        color = Colors.Transparent; string text = ColorInput?.Text?.Trim() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) return false;
        color = (Color)ColorConverter.ConvertFromString(text); return true;
    }
    private RgbCalibration CurrentCalibration=>new((int)RedGain.Value,(int)GreenGain.Value,(int)BlueGain.Value);
    private void ReduceGreen_Click(object sender,RoutedEventArgs e){RedGain.Value=100;GreenGain.Value=80;BlueGain.Value=100;}
    private void ResetCalibration_Click(object sender,RoutedEventArgs e){RedGain.Value=GreenGain.Value=BlueGain.Value=100;}
    private void SaveCalibration(RgbDevice device)
    {
        calibrations[device.Identity]=CurrentCalibration;
        AppFiles.Save(AppFiles.Load() with { RgbCalibrations=new(calibrations) });
    }
    private void Color_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (BrightnessLabel != null) BrightnessLabel.Text = $"밝기 {e.NewValue:F0}%"; UpdatePreview(); }
    private void UpdatePreview()
    {
        if (PreviewRing == null || PreviewGlow == null || BrightnessSlider == null || !TryColor(out var color)) return;
        if((RgbEffects?.SelectedItem as RgbMode)?.SupportsColor==false)
        {
            PreviewRing.Stroke=PreviewGlow.Stroke=Brushes.SlateGray;PreviewRing.Opacity=.7;PreviewGlow.Opacity=.15;
            if(RgbPreviewCaption!=null)RgbPreviewCaption.Text="자동 색상 효과 · 움직임 미리보기는 제공하지 않습니다.";return;
        }
        if(RgbPreviewCaption!=null)RgbPreviewCaption.Text="선택한 색 미리보기 · 실제 조명 상태와 별개";
        PreviewRing.Stroke = PreviewGlow.Stroke = new SolidColorBrush(color); PreviewRing.Opacity = BrightnessSlider.Value / 100; PreviewGlow.Opacity = BrightnessSlider.Value / 100 * 0.3;
    }
    private async void ApplyRgb_Click(object sender, RoutedEventArgs e)
    {
        if (RgbDevices.SelectedItem is not RgbDevice device || RgbEffects.SelectedItem is not RgbMode mode) return;
        bool hasColor=TryColor(out var color);
        if (!hasColor&&mode.SupportsColor) { RgbNotice.Text = "색상은 #53D9C5처럼 #과 6자리 HEX로 입력해 주세요."; return; }
        if(!hasColor)color=Colors.White;
        if (rgbBusy||rgbRefreshing||!rgbConnectionHealthy) return;
        string originalHex=$"#{color.R:X2}{color.G:X2}{color.B:X2}";var calibration=CurrentCalibration;
        try{SaveCalibration(device);}catch(Exception ex){RgbNotice.Text="보정 저장 실패: "+ex.Message;return;}
        color=CurrentCalibration.Apply(color);
        rgbBusy = true;rgbRestore.Cancel(device.Identity); UpdateRgbControls();
        int brightness = mode.SupportsBrightness?(int)BrightnessSlider.Value:100;
        int? speed=CurrentRgbSpeed;
        var zoneChoice=RgbZones.SelectedItem as RgbZoneChoice;
        bool transmitted=false;
        lastRgbWrite=DateTime.UtcNow;RgbNotice.Text="조명 설정을 보내고 실제 갱신을 기다리고 있어요…";
        try
        {
            var result=await RgbAsync(() => { using var client = new OpenRgbClient();return client.Apply(device, mode.Name, color, brightness,zoneChoice?.ZoneIds,speed); });
            transmitted=true;
            await Task.Delay(1500);
            var actualMode=result.Modes[result.ActiveMode].Name;
            RememberRgb(result,SavedRgbSetting.Capture(result,zoneChoice?.Key??"all",actualMode,originalHex,brightness,calibration,speed));
            AppFiles.Save(AppFiles.Load() with{Color=originalHex,Brightness=brightness,Effect=actualMode,RgbIdentity=result.Identity,RgbZone=zoneChoice?.Key??"all"});
            lastRgbError=null;RgbNotice.Text = $"{result.Name} / {zoneChoice?.Label??"장치 전체"}: {actualMode} 엔진 응답 확인 · 저장됨. 실제 조명은 직접 확인해 주세요.";
        }
        catch (Exception ex)
        {
            lastRgbError=ex.Message;
            if(!transmitted&&ex is IOException or TimeoutException)rgbConnectionHealthy=false;
            RgbNotice.Text = (transmitted?"엔진에 전송됐지만 설정 저장에 실패했어요: ":"적용 실패: ")+ex.Message+" · 멈췄다면 ‘효과 다시 시작’ 또는 ‘RGB 복구’를 사용해 주세요.";AppFiles.Log(ex.ToString());
        }
        finally { rgbBusy = false; UpdateRgbControls(); }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if(SelectedFanMode=="수동"&&!CommitManualInput())return;
        CommitCurveEdits();var hasValidCurve=TryEditorCurve(out var savedCurve);
        if(SelectedFanMode=="사용자 곡선"&&!hasValidCurve){StatusText.Text="곡선 입력값을 먼저 확인해 주세요.";return;}
        bool validColor=TryColor(out var savedColor);
        if (!validColor&&(RgbEffects.SelectedItem as RgbMode)?.SupportsColor!=false) { StatusText.Text = "색상을 #과 6자리 HEX로 입력해 주세요."; return; }
        if(!validColor)savedColor=Colors.White;
        try
        {
            if(!TryDisplayOptions(out _)){StatusText.Text="디스플레이 색상을 #과 6자리 HEX로 입력해 주세요.";return;}
            if(RgbDevices.SelectedItem is RgbDevice currentDevice)calibrations[currentDevice.Identity]=CurrentCalibration;
            var preferences=AppFiles.Load() with
            {
                Color=$"#{savedColor.R:X2}{savedColor.G:X2}{savedColor.B:X2}",Brightness=(RgbEffects.SelectedItem as RgbMode)?.SupportsBrightness==false?100:(int)BrightnessSlider.Value,
                Effect=(RgbEffects.SelectedItem as RgbMode)?.Name??"",FanMode=SelectedFanMode,Manual=ManualSlider.Value,
                DisplayColor=DisplayColorInput.Text.Trim().ToUpperInvariant(),DisplayBrightness=(int)DisplayBrightness.Value,
                DisplayEffect=DisplayEffect.SelectedIndex==1?"단색":"온도에 따라",CpuTemp=ShowCpuTemp.IsChecked==true,
                CpuLoad=ShowCpuLoad.IsChecked==true,GpuTemp=ShowGpuTemp.IsChecked==true,GpuLoad=ShowGpuLoad.IsChecked==true,
                RgbIdentity=(RgbDevices.SelectedItem as RgbDevice)?.Identity??"",RgbZone=(RgbZones.SelectedItem as RgbZoneChoice)?.Key??"all",
                RgbCalibrations=new(calibrations),CustomCurve=hasValidCurve?savedCurve:AppFiles.Load().CustomCurve
            };
            AppFiles.Save(preferences);
            if(RgbDevices.SelectedItem is RgbDevice savedDevice&&RgbEffects.SelectedItem is RgbMode savedMode)
                RememberRgb(savedDevice,SavedRgbSetting.Capture(savedDevice,preferences.RgbZone,savedMode.Name,preferences.Color,preferences.Brightness,CurrentCalibration,CurrentRgbSpeed));
            StatusText.Text="화면·조명 설정을 저장했어요. 팬·장치별 조명은 다음 실행에 자동 복원됩니다.";
        }
        catch (Exception ex) { StatusText.Text = "저장 실패: " + ex.Message; }
    }
    private async void Admin_Click(object sender, RoutedEventArgs e)
    {
        Send(new("stop")); worker?.StandardInput.Close();
        if (worker != null) await Task.Run(() => worker.WaitForExit(5000));
        try
        {
            // The existing instance releases its mutex on close; the new instance is delayed by this await/launch sequencing.
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            // Allow a short handover only for this explicit relaunch path.
            start.ArgumentList.Add("--handover");
            if(coolerDisplay?.Running==true)start.ArgumentList.Add("--display");
            if(rgbEngine is{HasExited:false}||RgbDevices.ItemsSource!=null)start.ArgumentList.Add("--rgb");
            if(!IsVisible)start.ArgumentList.Add("--background");
            Process.Start(start); RequestExit();
        }
        catch (Win32Exception) { FanNotice.Text = "관리자 실행을 취소했습니다. 센서 연결을 다시 시작합니다."; StartWorker(); }
    }
    private async void Driver_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "PawnIO_setup.exe");
        DriverButton.IsEnabled = false;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("드라이버 설치 파일이 없습니다. 배포 폴더 전체를 확인하세요.");
            using var installer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! });
            if (installer != null) await installer.WaitForExitAsync();
            StatusText.Text = "설치를 완료했다면 ‘관리자로 다시 열기’를 눌러 센서를 다시 감지하세요.";
        }
        catch (Exception ex) { StatusText.Text = "드라이버 설치를 진행하지 못했습니다: " + ex.Message; }
        finally { DriverButton.IsEnabled = true; }
    }
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "사용설명서.html");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show("배포 폴더의 사용설명서.html을 확인하세요.", "사용 설명서");
    }
    private void Logs_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(AppFiles.Data); Process.Start(new ProcessStartInfo("explorer.exe", AppFiles.Data) { UseShellExecute = true }); }
    private bool TryDisplayOptions(out DisplayOptions options)
    {
        options=null!;var text=DisplayColorInput.Text.Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(text,"^#[0-9a-fA-F]{6}$"))return false;
        options=new((Color)ColorConverter.ConvertFromString(text),(int)DisplayBrightness.Value,DisplayEffect.SelectedIndex==0,ShowCpuTemp.IsChecked==true,ShowCpuLoad.IsChecked==true,ShowGpuTemp.IsChecked==true,ShowGpuLoad.IsChecked==true);return true;
    }
    private void DisplayConnect_Click(object sender,RoutedEventArgs e)
    {
        if(!TryDisplayOptions(out var options)){DisplayNotice.Text="디스플레이 색상은 #과 6자리 HEX로 입력해 주세요.";return;}
        try
        {
            coolerDisplay?.Dispose();coolerDisplay=new(options);
            coolerDisplay.Status+=message=>Dispatcher.BeginInvoke(()=>{if(!closing)DisplayNotice.Text=message;});
            if(latest!=null)coolerDisplay.Update(DisplayMetrics.From(latest),options);
            coolerDisplay.Start();DisplayNotice.Text="PA120 DIGITAL에 온도·사용률 전송을 시작합니다.";
        }
        catch(Exception ex){DisplayNotice.Text=ex.Message;AppFiles.Log("DISPLAY "+ex.Message);}
    }
    private void DisplayStop_Click(object sender,RoutedEventArgs e){coolerDisplay?.Dispose();coolerDisplay=null;DisplayNotice.Text="전송을 중지했습니다. 화면에는 마지막 숫자가 남을 수 있습니다. TRCC를 다시 열면 원래 제어를 이어갈 수 있어요.";}
    private void OpenTrcc_Click(object sender,RoutedEventArgs e)
    {
        DisplayStop_Click(sender,e);var path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"TRCCCAP","TRCC.exe");
        try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true,WorkingDirectory=System.IO.Path.GetDirectoryName(path)!});}catch(Exception ex){DisplayNotice.Text="TRCC 열기 실패: "+ex.Message;}
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if(!exitRequested){e.Cancel=true;HideToTray(true);return;}
        closing = true; timer.Stop(); Send(new("stop"));
        coolerDisplay?.Dispose();
        try { worker?.StandardInput.Close(); worker?.WaitForExit(4000); } catch { }
        try { if (rgbEngine is { HasExited: false }) { var remaining=TimeSpan.FromSeconds(3)-(DateTime.UtcNow-lastRgbWrite);if(remaining>TimeSpan.Zero)Thread.Sleep(remaining);rgbEngine.Kill(); } } catch { }
        worker?.Dispose(); rgbEngine?.Dispose();
        worker=null;rgbEngine=null;WriteRuntimeStatus();
    }
}
