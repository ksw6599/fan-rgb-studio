using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;

namespace FanRgbStudio;

// Entire request is validated before the first mode/LED write. Flags are from the negotiated SDK3 mode.
public record RgbWritePlan(byte[] Raw,uint PackedColor,uint ColorMode,uint? Speed,uint? Brightness)
{
    public static RgbWritePlan Build(RgbMode mode,Color color,int brightness,bool zone,int? speedPercent=null)
    {
        if(brightness is <0 or >100||speedPercent is <0 or >100)throw new InvalidDataException("RGB 밝기·속도는 0~100 사이로 설정해 주세요.");
        if(zone&&!mode.SupportsZoneColor)throw new InvalidDataException("이 효과는 장치 전체에 적용해야 합니다. 조명 구역을 ‘장치 전체’로 선택해 주세요.");
        var raw=mode.Raw.ToArray();int f=mode.FieldsOffset;
        void Set(int field,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(f+field*4),value);
        uint colorMode=0;
        if(zone)colorMode=1;
        else if(mode.Field(11)==2&&(mode.Flags&0x40)!=0&&mode.Field(7)>0)colorMode=2;
        else if((mode.Flags&0x20)!=0)colorMode=1;
        else if((mode.Flags&0x40)!=0&&mode.Field(7)>0)colorMode=2;
        else if(mode.Field(11)==3&&(mode.Flags&0x80)!=0)colorMode=3;
        uint? targetBrightness=null,targetSpeed=null;
        if((mode.Flags&0x10)!=0&&!zone)
        {
            if(mode.Field(5)<mode.Field(4))throw new InvalidDataException("장치의 RGB 밝기 범위가 올바르지 않습니다.");
            targetBrightness=(uint)Math.Round(mode.Field(4)+(mode.Field(5)-(double)mode.Field(4))*brightness/100.0,MidpointRounding.AwayFromZero);Set(9,targetBrightness.Value);
        }
        if((mode.Flags&1)!=0)
        {
            uint min=mode.Field(2),max=mode.Field(3);
            if(max<min)throw new InvalidDataException("장치의 효과 속도 범위가 올바르지 않습니다.");
            uint speed=mode.Field(8);
            if(speedPercent!=null)speed=(uint)Math.Round(min+(max-(double)min)*speedPercent.Value/100.0,MidpointRounding.AwayFromZero);
            else if(speed<min||speed>max)speed=(uint)(min+(max-(ulong)min)/2);
            targetSpeed=speed;Set(8,speed);
        }
        else if(speedPercent!=null)throw new InvalidDataException("이 효과는 속도 조절을 지원하지 않습니다.");
        var directions=new List<uint>();
        if((mode.Flags&2)!=0)directions.AddRange([0,1]);if((mode.Flags&4)!=0)directions.AddRange([2,3]);
        if((mode.Flags&8)!=0)directions.AddRange([4,5]);if((mode.Flags&0x800)!=0)directions.AddRange([6,7,8,9]);
        if(!directions.Contains(mode.Field(10)))Set(10,directions.FirstOrDefault());
        bool manualColor=colorMode is 1 or 2;
        if(brightness==0&&!manualColor&&targetBrightness==null&&!mode.Name.Equals("Off",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("이 효과는 밝기 0 또는 끄기를 지원하지 않습니다. 다른 효과를 선택해 주세요.");
        double scale=targetBrightness!=null?1:brightness/100.0;
        uint rgb=(uint)((int)(color.R*scale)|((int)(color.G*scale)<<8)|((int)(color.B*scale)<<16));
        if(colorMode==2)
        {
            uint min=mode.Field(6),max=mode.Field(7);int count=mode.Colors;
            if(max<min||max==0||max>1024)throw new InvalidDataException("RGB 효과 색상 수 범위가 올바르지 않습니다.");
            count=Math.Clamp(Math.Max(count,(int)Math.Max(1,min)),1,(int)max);
            Array.Resize(ref raw,f+50+count*4);BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(f+48),(ushort)count);
            // A single HEX editor changes the first palette color. Flattening every entry would
            // make multicolor waves/gradients look frozen even though the native effect is running.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(f+50),rgb);
        }
        Set(11,colorMode);
        return new(raw,rgb,colorMode,targetSpeed,targetBrightness);
    }
    public bool Matches(RgbMode mode)
    {
        if(mode.Field(11)!=ColorMode||mode.Field(10)!=BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(mode.FieldsOffset+40))||Speed is uint s&&mode.Field(8)!=s||Brightness is uint b&&mode.Field(9)!=b)return false;
        if(ColorMode!=2)return true;
        var count=BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(mode.FieldsOffset+48));
        return mode.Colors==count&&Enumerable.Range(0,count).All(i=>BinaryPrimitives.ReadUInt32LittleEndian(mode.Raw.AsSpan(mode.FieldsOffset+50+i*4))==BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(mode.FieldsOffset+50+i*4)));
    }
}
