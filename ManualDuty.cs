using System.Globalization;

namespace FanRgbStudio;

public static class ManualDuty
{
    public static bool TryParse(string text,out double value)
    {
        if(!double.TryParse(text.Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)||!double.IsFinite(value)||value<30||value>100)return false;
        value=Math.Round(value,1,MidpointRounding.AwayFromZero);return true;
    }
    public static double Step(double value,double step)=>Math.Clamp(Math.Round(value+step,1,MidpointRounding.AwayFromZero),30,100);
}
