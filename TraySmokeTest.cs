using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FanRgbStudio;

public static class TraySmokeTest
{
    public static bool Success{get;private set;}
    public static async Task Run(MainWindow window,string output)
    {
        var checks=new List<string>();int? worker=null;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);checks.Add("PASS "+name);}
        try
        {
            for(int n=0;n<60&&window.LastSensorFrame==DateTime.MinValue;n++)await Task.Delay(250);
            Check(window.LastSensorFrame!=DateTime.MinValue,"real sensor worker produces an initial frame");
            worker=window.WorkerProcessId;
            Check(worker.HasValue,"worker process is running");
            Check(!window.IsVisible&&!window.ShowInTaskbar&&window.TrayVisible,"background startup hides window and keeps tray icon");
            window.ShowSettings();Check(window.IsVisible&&window.ShowInTaskbar,"settings window restores from tray");
            var mode=(ComboBox)window.FindName("FanMode");var slider=(Slider)window.FindName("ManualSlider");
            var editor=(WrapPanel)window.FindName("ManualEditor");var number=(TextBox)window.FindName("ManualValueInput");
            var oldMode=mode.SelectedIndex;var oldValue=slider.Value;mode.SelectedIndex=3;
            Check(editor.IsEnabled&&slider.SmallChange==0.1&&slider.LargeChange==1&&slider.IsSnapToTickEnabled,"manual controls support fine keyboard and slider steps");
            void Enter()=>number.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
            number.Text="35.7";Enter();Check(Math.Abs(slider.Value-35.7)<0.0001,"numeric Enter commits a fractional duty without truncation");
            editor.Children.OfType<Button>().Single(b=>b.Tag?.ToString()=="0.1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Math.Abs(slider.Value-35.8)<0.0001,"plus 0.1 button changes the numeric input and slider together");
            number.Text="10";Enter();Check(Math.Abs(slider.Value-35.8)<0.0001,"invalid numeric duty cannot replace the current output");
            number.Text="35.8";
            await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Render);
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using(var imageFile=File.Create(Path.ChangeExtension(output,".png")))png.Save(imageFile);
            mode.SelectedIndex=4;
            var panel=(StackPanel)window.FindName("CustomCurvePanel");var grid=(DataGrid)window.FindName("CustomCurveGrid");
            Check(panel.Visibility==Visibility.Visible&&grid.Items.Count>=2,"custom curve editor is available as a separate fan profile");
            var curveRow=(CurveEditorRow)grid.Items[2];grid.ScrollIntoView(curveRow);grid.UpdateLayout();grid.CurrentCell=new DataGridCellInfo(curveRow,grid.Columns[1]);
            Check(grid.BeginEdit(),"temperature point duty cell enters editing");await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Render);
            var cellEditor=grid.Columns[1].GetCellContent(curveRow) as TextBox;
            Check(cellEditor!=null,"curve cell provides an editable numeric field");
            cellEditor!.SetCurrentValue(TextBox.TextProperty,"52");cellEditor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);
            Check(curveRow.Duty=="52","editing a curve cell updates its configured output");
            var pointButtons=panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();int count=grid.Items.Count;
            pointButtons.Single(b=>b.Content?.ToString()=="지점 추가").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(grid.Items.Count==count+1,"add point inserts a valid intermediate temperature");
            pointButtons.Single(b=>b.Content?.ToString()=="선택 삭제").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(grid.Items.Count==count,"selected intermediate point can be removed");
            grid.SelectedIndex=grid.Items.Count-1;pointButtons.Single(b=>b.Content?.ToString()=="선택 삭제").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(grid.Items.Count==count,"85C safety endpoint cannot be deleted");
            panel.BringIntoView();await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Render);
            var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(panel);
            var curveBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(panel.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,System.Windows.Media.PixelFormats.Pbgra32);
            curveBitmap.Render(panel);var curvePng=new System.Windows.Media.Imaging.PngBitmapEncoder();curvePng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(curveBitmap));
            using(var imageFile=File.Create(Path.ChangeExtension(output,"curve.png")))curvePng.Save(imageFile);
            pointButtons.Single(b=>b.Content?.ToString()=="기본 곡선").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            slider.Value=oldValue;number.Text=oldValue.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);mode.SelectedIndex=oldMode;
            var before=window.LastSensorFrame;
            window.Close();Check(!window.IsVisible&&!window.IsStopping&&window.TrayVisible,"X close hides instead of shutting down");
            await Task.Delay(2500);
            Check(window.WorkerProcessId==worker&&window.LastSensorFrame>before,"same worker and heartbeats continue while hidden");
            var launch=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};launch.ArgumentList.Add("--show");
            using(var second=Process.Start(launch)!){await second.WaitForExitAsync();Check(second.ExitCode==0,"second launch sends a restore signal and exits");}
            for(int n=0;n<20&&!window.IsVisible;n++)await Task.Delay(100);
            Check(window.IsVisible&&window.WorkerProcessId==worker,"second launch restores existing window without replacing worker");
            window.WindowState=WindowState.Minimized;
            for(int n=0;n<20&&window.IsVisible;n++)await Task.Delay(50);
            Check(!window.IsVisible&&window.TrayVisible,$"minimize sends window to tray (visible={window.IsVisible}, state={window.WindowState}, tray={window.TrayVisible})");
            bool closed=false;window.Closed+=(_,_)=>closed=true;window.RequestExit();
            Check(closed&&!window.TrayVisible,"explicit exit closes window and disposes tray icon");
            if(worker.HasValue){try{using var child=Process.GetProcessById(worker.Value);Check(child.HasExited,"explicit exit ends the worker");}catch(ArgumentException){checks.Add("PASS explicit exit ends the worker");}}
            Success=true;
        }
        catch(Exception ex){checks.Add("FAIL "+ex);}
        finally
        {
            File.WriteAllText(output,JsonSerializer.Serialize(new{Success,Checks=checks,WorkerPid=worker,HardwareWrites=false},new JsonSerializerOptions{WriteIndented=true}));
            if(!window.IsStopping)window.RequestExit();
        }
    }
}
