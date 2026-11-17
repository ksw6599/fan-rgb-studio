using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace FanRgbStudio;

public static class Program
{
    private const string ShowEvent="Local\\SunuongFanStudio.ShowSettings";
    public static bool TryShowExisting(string eventName=ShowEvent)
    {
        try{using var signal=EventWaitHandle.OpenExisting(eventName);signal.Set();return true;}
        catch(Exception ex)when(ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException){return false;}
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--worker")) return HardwareWorker.Run(!args.Contains("--no-fan-restore"));
            if (args.Contains("--self-test")) return SelfTests.Run(args);
            if (args.Contains("--probe")) return HardwareWorker.Probe(args);
            if (args.Contains("--device-inventory")) return DeviceDiagnostics.Inventory(args);
            if (args.Contains("--rgb-debug")) return DeviceDiagnostics.RgbDebug(args);
            if (args.Contains("--apply-saved-rgb")) return DeviceDiagnostics.ApplySavedRgb(args);
            if (args.Contains("--display-debug")) return DeviceDiagnostics.DisplayDebug(args);
            bool trayTest=args.Contains("--tray-smoke-test");
            if(args.Contains("--smoke-peer"))return TryShowExisting(ShowEvent+".Smoke")?0:1;
            string eventName=trayTest?ShowEvent+".Smoke":ShowEvent;
            if(args.Contains("--background")&&!AppFiles.IsAdmin&&!trayTest)
            {
                if(TryShowExisting())return 0;
                try
                {
                    var elevated=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
                    foreach(var arg in args)elevated.ArgumentList.Add(arg);
                    Process.Start(elevated);return 0;
                }
                catch(Win32Exception ex)when(ex.NativeErrorCode==1223){AppFiles.Log("Background launch cancelled by user.");return 0;}
            }
            using var mutex = new Mutex(false, "Local\\SunuongFanStudio.UI"+(trayTest?".Smoke":""));
            bool first;
            try { first = mutex.WaitOne(args.Contains("--handover") ? 10000 : 0); } catch (AbandonedMutexException) { first = true; }
            if (!first) { if(!TryShowExisting(eventName))MessageBox.Show("Fan Studio가 시작 중입니다. 잠시 후 트레이 아이콘을 더블클릭해 주세요.", "Fan Studio"); return 0; }
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) => { AppFiles.Log(e.Exception.ToString()); MessageBox.Show(e.Exception.Message, "Fan Studio 오류"); e.Handled = true; };
            using var showSignal=new EventWaitHandle(false,EventResetMode.AutoReset,eventName);
            int resumeAt=Array.IndexOf(args,"--resume-session");
            var resumeFile=resumeAt>=0&&resumeAt+1<args.Length?args[resumeAt+1]:null;
            var window=new MainWindow(args.Contains("--display"),args.Contains("--background")||trayTest,resumeFile,args.Contains("--rgb"),!trayTest);
            var registered=ThreadPool.RegisterWaitForSingleObject(showSignal,(_,_)=>app.Dispatcher.BeginInvoke(window.ShowSettings),null,Timeout.Infinite,false);
            app.SessionEnding+=(_,_)=>window.AllowSessionExit();
            if(trayTest)window.Loaded+=async(_,_)=>await TraySmokeTest.Run(window,DeviceDiagnostics.Output(args));
            try { app.Run(window); } finally { registered.Unregister(null);mutex.ReleaseMutex(); }
            return trayTest&&!TraySmokeTest.Success?1:0;
        }
        catch (Exception ex) { AppFiles.Log(ex.ToString()); return 1; }
    }
}
