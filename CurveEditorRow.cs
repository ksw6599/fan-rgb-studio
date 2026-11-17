using System.ComponentModel;
using System.Globalization;

namespace FanRgbStudio;

public sealed class CurveEditorRow:INotifyPropertyChanged
{
    private string temperature,duty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Temperature{get=>temperature;set{temperature=value;PropertyChanged?.Invoke(this,new(nameof(Temperature)));}}
    public string Duty{get=>duty;set{duty=value;PropertyChanged?.Invoke(this,new(nameof(Duty)));}}
    public CurveEditorRow(FanCurvePoint point){temperature=point.Temperature.ToString("0.#",CultureInfo.InvariantCulture);duty=point.Duty.ToString("0.#",CultureInfo.InvariantCulture);}
    public bool TryPoint(out FanCurvePoint point)
    {
        point=null!;
        if(!float.TryParse(Temperature.Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var temp)||
            !float.TryParse(Duty.Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))return false;
        point=new(temp,value);return true;
    }
}
