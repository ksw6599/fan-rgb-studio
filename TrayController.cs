using System.Runtime.InteropServices;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;

namespace FanRgbStudio;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Drawing.Icon drawing;
    private bool disposed;
    public bool Visible => !disposed && icon.Visible;
    public TrayController(Action show,Action hide,Action exit)
    {
        drawing=CreateIcon();menu=new();
        menu.Items.Add("설정 열기",null,(_,_)=>show());
        menu.Items.Add("백그라운드로 보내기",null,(_,_)=>hide());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("완전히 종료",null,(_,_)=>exit());
        icon=new Forms.NotifyIcon{Icon=drawing,Text="선웅 Fan Studio · 팬 / 쿨러 디스플레이",ContextMenuStrip=menu,Visible=true};
        icon.DoubleClick+=(_,_)=>show();
    }
    public void ExplainBackground()
    {
        if(disposed)return;
        icon.ShowBalloonTip(3500,"선웅 Fan Studio","창을 닫아도 계속 작동합니다. 트레이 아이콘을 더블클릭하면 설정이 열립니다.",Forms.ToolTipIcon.Info);
    }
    private static Drawing.Icon CreateIcon()
    {
        using var bitmap=new Drawing.Bitmap(32,32);
        using(var g=Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode=Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(Drawing.Color.Transparent);
            using var ring=new Drawing.Pen(Drawing.Color.FromArgb(83,217,197),3);
            using var blade=new Drawing.SolidBrush(Drawing.Color.FromArgb(83,217,197));
            using var hub=new Drawing.SolidBrush(Drawing.Color.FromArgb(27,32,43));
            g.DrawEllipse(ring,3,3,26,26);
            for(int i=0;i<3;i++)g.FillPie(blade,8,8,16,16,30+i*120,80);
            g.FillEllipse(hub,13,13,6,6);
        }
        var handle=bitmap.GetHicon();
        try{using var native=Drawing.Icon.FromHandle(handle);return(Drawing.Icon)native.Clone();}
        finally{DestroyIcon(handle);}
    }
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr handle);
    public void Dispose(){if(disposed)return;disposed=true;icon.Visible=false;icon.Dispose();menu.Dispose();drawing.Dispose();}
}
