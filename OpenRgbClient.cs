using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Windows.Media;

namespace FanRgbStudio;

public record RgbMode(string Name, int Index, uint Flags, byte[] Raw, int FieldsOffset, ushort Colors)
{
    public uint Field(int field)=>field is >=0 and <12&&Raw.Length>=FieldsOffset+48?BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(FieldsOffset+field*4)):throw new InvalidDataException("RGB 효과 정보가 불완전합니다.");
    public bool SupportsColor=>(Flags&0x20)!=0||((Flags&0x40)!=0&&Field(7)>0);
    public bool SupportsSpeed=>(Flags&1)!=0&&Field(3)>Field(2);
    public bool SupportsBrightness=>(Flags&0x10)!=0||SupportsColor;
    public bool SupportsZoneColor=>(Flags&0x20)!=0&&(Flags&0x400)==0;
    public bool IsSolid=>Name.Equals("Static",StringComparison.OrdinalIgnoreCase)||Name.Equals("Direct",StringComparison.OrdinalIgnoreCase);
    public int DefaultSpeedPercent(bool active)=>SupportsSpeed&&active&&Field(8)>=Field(2)&&Field(8)<=Field(3)?(int)Math.Round((Field(8)-Field(2))*100.0/(Field(3)-Field(2))):50;
}
public record RgbZone(uint Index,string Name,int Type,uint Minimum,uint Maximum,uint Leds,int Start)
{
    public bool Addressable=>Name.Contains("Addressable",StringComparison.OrdinalIgnoreCase)||Name.Contains("ARGB",StringComparison.OrdinalIgnoreCase);
    public string Label=>Name.Replace("Addressable Header","ARGB 헤더",StringComparison.OrdinalIgnoreCase)+$" · 설정 {Leds} LED";
}
public record RgbZoneChoice(string Key,string Label,uint[]? ZoneIds)
{
    public static List<RgbZoneChoice> For(RgbDevice device)
    {
        List<RgbZoneChoice> choices=[new("all","장치 전체",null)];
        var zones=device.Zones??[];var argb=zones.Where(z=>z.Addressable&&z.Leds>0).ToArray();
        if(argb.Length>0)choices.Add(new("argb","시스템 팬 · ARGB 헤더 전체",argb.Select(z=>z.Index).ToArray()));
        choices.AddRange(zones.Where(z=>z.Leds>0).Select(z=>new RgbZoneChoice("zone:"+z.Index,z.Label,[z.Index])));return choices;
    }
}
public record RgbDevice(uint Index, string Name, string Vendor, string Serial, string Location, ushort Leds, List<RgbMode> Modes, int ActiveMode = -1, uint[]? CurrentColors = null,List<RgbZone>? Zones=null,int DeviceType=-1)
{
    public string Identity => Name + "|" + Serial + "|" + Location;
    public override string ToString() => Name;
    public bool HasCaseFanHeaders=>DeviceType==0&&Zones?.Any(z=>z.Addressable&&z.Leds>0)==true;
    public string Label=>HasCaseFanHeaders?"시스템 팬 / 메인보드 · "+Name:Name;
}

// Uses the documented SDK at negotiated protocol 3, over loopback only.
public sealed class OpenRgbClient : IDisposable
{
    private readonly TcpClient client = new();
    private readonly Stopwatch elapsed=Stopwatch.StartNew();
    private readonly TimeSpan budget;
    private NetworkStream stream = null!;
    public OpenRgbClient(int port = 6742,TimeSpan? operationBudget=null)
    {
        budget=operationBudget??TimeSpan.FromSeconds(20);
        try
        {
            client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,Math.Min(3000,budget.TotalMilliseconds)))).GetAwaiter().GetResult();
            stream = client.GetStream(); stream.ReadTimeout = 12000; stream.WriteTimeout = 4000;
            Send(0, 40, BitConverter.GetBytes(3u));
            var response = Receive(40);
            if (response.Length != 4 || BitConverter.ToUInt32(response) < 3) throw new IOException("OpenRGB SDK 프로토콜 3 이상이 필요합니다.");
            Send(0, 50, Encoding.UTF8.GetBytes("Sunuong Fan Studio\0"));
        }
        catch { client.Dispose(); throw; }
    }
    private void Send(uint device, uint command, byte[] payload)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        writer.Write(Encoding.ASCII.GetBytes("ORGB")); writer.Write(device); writer.Write(command); writer.Write((uint)payload.Length); writer.Write(payload);
        stream.WriteTimeout=RemainingTimeout(4000);stream.Write(buffer.ToArray());
    }
    private int RemainingTimeout(int maximum)
    {
        var milliseconds=(budget-elapsed.Elapsed).TotalMilliseconds;
        if(milliseconds<=0)throw new TimeoutException("RGB 엔진 응답 시간이 초과됐습니다. RGB 복구로 다시 연결해 주세요.");
        return Math.Max(1,(int)Math.Min(maximum,milliseconds));
    }
    private void ReadBounded(Span<byte> buffer)
    {
        int offset=0;
        while(offset<buffer.Length)
        {
            stream.ReadTimeout=RemainingTimeout(12000);
            int read=stream.Read(buffer[offset..]);if(read==0)throw new EndOfStreamException("RGB 엔진 연결이 끊겼습니다.");offset+=read;
        }
    }
    private byte[] Receive(uint expected, uint? device = null)
    {
        for (int attempts = 0; attempts < 32; attempts++)
        {
            var header = new byte[16]; ReadBounded(header);
            if (Encoding.ASCII.GetString(header, 0, 4) != "ORGB") throw new IOException("올바르지 않은 RGB 서버 응답입니다.");
            uint id = BitConverter.ToUInt32(header, 8), size = BitConverter.ToUInt32(header, 12);
            if (size > 4 * 1024 * 1024) throw new IOException("RGB 응답이 너무 큽니다.");
            byte[] payload = new byte[size]; ReadBounded(payload);
            if (id == 100) throw new IOException("RGB 장치 목록이 변경되었습니다. 다시 검색해 주세요.");
            if (id == expected && (device == null || BitConverter.ToUInt32(header, 4) == device)) return payload;
        }
        throw new IOException("RGB 서버 응답 순서를 확인할 수 없습니다.");
    }
    public List<RgbDevice> Devices()
    {
        Send(0, 0, []); var bytes = Receive(0);
        if (bytes.Length != 4) throw new IOException("RGB 장치 수 응답이 올바르지 않습니다.");
        uint count = BitConverter.ToUInt32(bytes);
        if (count > 256) throw new IOException("RGB 장치가 너무 많습니다.");
        List<RgbDevice> devices = [];
        for (uint i = 0; i < count; i++) devices.Add(GetDevice(i));
        return devices;
    }
    private RgbDevice GetDevice(uint index)
    {
        Send(index, 1, BitConverter.GetBytes(3u));
        return Parse(index, Receive(1, index));
    }
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadUInt16(); var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }
    public static RgbDevice Parse(uint index, byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes); using var reader = new BinaryReader(buffer);
        if (reader.ReadUInt32() != bytes.Length) throw new IOException("RGB 장치 데이터 길이가 맞지 않습니다.");
        int deviceType=reader.ReadInt32();
        string name = ReadString(reader), vendor = ReadString(reader);
        ReadString(reader); ReadString(reader);
        string serial = ReadString(reader), location = ReadString(reader);
        int count = reader.ReadUInt16(); int activeMode=reader.ReadInt32();
        List<RgbMode> modes = [];
        for (int i = 0; i < count; i++)
        {
            int start = (int)buffer.Position; string modeName = ReadString(reader); int fields = (int)buffer.Position - start;
            reader.ReadUInt32(); uint flags = reader.ReadUInt32();
            for (int n = 0; n < 10; n++) reader.ReadUInt32();
            ushort colors = reader.ReadUInt16();
            if (reader.ReadBytes(colors * 4).Length != colors * 4) throw new EndOfStreamException();
            modes.Add(new(modeName, i, flags, bytes[start..(int)buffer.Position], fields, colors));
        }
        int zones = reader.ReadUInt16();List<RgbZone> zoneList=[];int zoneStart=0;
        for (int i = 0; i < zones; i++)
        {
            string zoneName=ReadString(reader);int zoneType=reader.ReadInt32();uint min=reader.ReadUInt32(),max=reader.ReadUInt32(),ledCount=reader.ReadUInt32();
            if(ledCount>65535)throw new IOException("RGB 구역의 LED 수가 너무 큽니다.");
            zoneList.Add(new((uint)i,zoneName,zoneType,min,max,ledCount,zoneStart));zoneStart=checked(zoneStart+(int)ledCount);
            int matrixBytes = reader.ReadUInt16();
            if (reader.ReadBytes(matrixBytes).Length != matrixBytes) throw new EndOfStreamException();
        }
        ushort leds = reader.ReadUInt16();
        for (int i = 0; i < leds; i++) { ReadString(reader); reader.ReadUInt32(); }
        int colorCount = reader.ReadUInt16();
        var currentColors=new uint[colorCount];for(int i=0;i<colorCount;i++)currentColors[i]=reader.ReadUInt32();
        if (buffer.Position != buffer.Length || colorCount != leds||zoneList.Any(z=>z.Start+z.Leds>leds))
            throw new IOException("RGB LED 데이터가 올바르지 않습니다.");
        return new(index, name, vendor, serial, location, leds, modes,activeMode,currentColors,zoneList,deviceType);
    }
    public RgbDevice Apply(RgbDevice expected, string modeName, Color color, int brightness,IReadOnlyList<uint>? zoneIds=null,int? speedPercent=null)
    {
        // Re-read identity before writing: never apply a stale index to a different device.
        var current = GetDevice(expected.Index);
        if (current.Identity != expected.Identity) throw new IOException("RGB 장치가 변경되었습니다. 다시 검색해 주세요.");
        List<RgbZone>? selected=null;
        if(zoneIds!=null)
        {
            if(zoneIds.Count==0)throw new IOException("조명 구역을 선택해 주세요.");
            selected=[];
            foreach(var id in zoneIds.Distinct())
            {
                var zone=current.Zones?.SingleOrDefault(z=>z.Index==id&&z.Leds>0)??throw new IOException("선택한 RGB 구역을 찾지 못했습니다. 다시 검색해 주세요.");
                var old=expected.Zones?.SingleOrDefault(z=>z.Index==id);
                if(old==null||old.Name!=zone.Name||old.Start!=zone.Start||old.Leds!=zone.Leds)throw new IOException("RGB 구역 구성이 변경되었습니다. 다시 검색해 주세요.");
                selected.Add(zone);
            }
        }
        // Polychrome USB Direct needs WriteAllZones; UpdateZoneLEDs only calls WriteZone.
        // Use its native Static mode for uniform header colors rather than acknowledging an ineffective Direct write.
        modeName=ZoneCompatibleMode(current,modeName,selected!=null);
        if(brightness<=0&&selected==null&&current.Modes.Any(m=>m.Name.Equals("Off",StringComparison.OrdinalIgnoreCase)))modeName=current.Modes.First(m=>m.Name.Equals("Off",StringComparison.OrdinalIgnoreCase)).Name;
        var mode = current.Modes.SingleOrDefault(m => m.Name == modeName) ?? throw new IOException("이 장치에서 제공하지 않는 효과입니다.");
        var plan=RgbWritePlan.Build(mode,color,brightness,selected!=null,speedPercent);
        var raw=plan.Raw;uint rgb=plan.PackedColor;
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        writer.Write((uint)(raw.Length + 8)); writer.Write(mode.Index); writer.Write(raw);
        Send(current.Index, 1101, buffer.ToArray());
        if (plan.ColorMode==1 && current.Leds > 0)
        {
            if(selected==null)
            {
                using var colors = new MemoryStream(); using var w = new BinaryWriter(colors);
                w.Write((uint)(6 + current.Leds * 4)); w.Write(current.Leds);
                for (int i = 0; i < current.Leds; i++) w.Write(rgb);
                Send(current.Index, 1050, colors.ToArray());
            }
            else foreach(var zone in selected)
            {
                using var colors=new MemoryStream();using var w=new BinaryWriter(colors);
                w.Write((uint)(10+zone.Leds*4));w.Write(zone.Index);w.Write((ushort)zone.Leds);
                for(int i=0;i<zone.Leds;i++)w.Write(rgb);
                Send(current.Index,1051,colors.ToArray());
            }
        }
        // Verify the server actually accepted the mode and color. This is not a physical LED acknowledgement.
        RgbDevice readback=current;bool accepted=false;
        for(int attempt=0;attempt<3;attempt++)
        {
            readback=GetDevice(current.Index);
            if(readback.Identity!=current.Identity)throw new IOException("RGB 장치가 변경되었습니다. 다시 검색해 주세요.");
            bool colorsOk=plan.ColorMode!=1||current.Leds==0||(selected==null?readback.CurrentColors?.All(c=>c==rgb)==true:selected.All(z=>readback.CurrentColors?.Skip(z.Start).Take((int)z.Leds).Count(c=>c==rgb)==z.Leds));
            bool modeValuesOk=readback.Modes.Any(m=>m.Index==mode.Index&&m.Name==mode.Name&&plan.Matches(m));
            accepted=readback.ActiveMode==mode.Index&&colorsOk&&modeValuesOk;if(accepted)break;
            Thread.Sleep(150);
        }
        if(!accepted)throw new IOException("RGB 엔진이 선택한 효과/구역 색상을 반영하지 않았습니다. 다시 연결해 주세요.");
        return readback;
    }
    public void Dispose() => client.Dispose();
    public static string ZoneCompatibleMode(RgbDevice device,string mode,bool hasZones) =>
        hasZones && device.HasCaseFanHeaders && device.Name.StartsWith("ASRock",StringComparison.OrdinalIgnoreCase) &&
        device.Location.StartsWith("HID:",StringComparison.OrdinalIgnoreCase) && mode.Equals("Direct",StringComparison.OrdinalIgnoreCase)
        ? device.Modes.FirstOrDefault(m=>m.Name.Equals("Static",StringComparison.OrdinalIgnoreCase))?.Name??mode : mode;
}
