using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace FanRgbStudio;

public static class SelfTests
{
    public static int Run(string[] args)
    {
        var lines = new List<string>(); int passed = 0, failed = 0;
        void Check(string name, Action test)
        {
            try { test(); lines.Add("PASS " + name); passed++; }
            catch (Exception ex) { lines.Add("FAIL " + name + ": " + ex); failed++; }
        }
        void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }
        Check("all curves remain monotonic, >=30%, <=100%", () =>
        {
            foreach (var mode in new[] { "저소음", "균형", "성능" })
            {
                float previous = 0;
                for (float t = 1; t <= 120; t += .1f) { float v = FanCurve.Evaluate(mode, 50, t); Assert(v >= previous - .001f && v >= 30 && v <= 100, mode); previous = v; }
            }
        });
        Check("85C overrides manual duty, including lowest setting", () => Assert(FanCurve.Evaluate("수동", 30, 85) == 100 && FanCurve.Evaluate("저소음", 30, 100) == 100, "overheat"));
        Check("cool temperatures retain the first curve point", () => Assert(FanCurve.Evaluate("균형", 30, 20) == 40 && FanCurve.Evaluate("성능", 30, 10) == 50, "cold floor"));
        Check("invalid temperature requests maximum cooling", () => Assert(FanCurve.Evaluate("수동", 40, float.NaN) == 100 && FanCurve.Evaluate("수동", 40, 0) == 100, "invalid"));
        Check("hardware duty bounds are honored", () => Assert(FanCurve.Evaluate("수동", 0, 40, 45, 90) == 45 && FanCurve.Evaluate("수동", 120, 40, 45, 90) == 90, "bounds"));
        Check("missing CPU never falls back to unrelated sensors", () =>
        {
            var snapshot = new Snapshot(true, "Test", [new("/gpu-nvidia/0/temperature/0", "GPU", "GPU", "Temperature", 50), new("/lpc/temp", "Board", "Board", "Temperature", 35)], [], "", DateTime.UtcNow);
            Assert(HardwareWorker.SafetyTemperature(snapshot) == null, "missing CPU");
            snapshot.Sensors.Add(new("/amdcpu/0/temperature/0", "CPU", "CPU", "Temperature", 45));
            Assert(HardwareWorker.SafetyTemperature(snapshot) == 50, "CPU/GPU maximum");
        });
        Check("fractional manual duty survives input, preferences and worker commands",()=>
        {
            Assert(ManualDuty.TryParse("35.7",out var value)&&value==35.7&&ManualDuty.TryParse("35,7",out var comma)&&comma==value,"decimal input");
            var prefs=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(new Preferences(Manual:value)))!;
            var command=JsonSerializer.Deserialize<WorkerCommand>(JsonSerializer.Serialize(new WorkerCommand("set","fan1","수동",(float)prefs.Manual)))!;
            Assert(Math.Abs(command.Value-35.7f)<0.0001&&FanCurve.Evaluate(command.Mode,command.Value,40)==command.Value,"no integer truncation");
            foreach(var invalid in new[]{"NaN","Infinity","29.9","100.1","35abc","","1e2","35.7.2"})Assert(!ManualDuty.TryParse(invalid,out _),"reject "+invalid);
            double stepped=30;for(int i=0;i<57;i++)stepped=ManualDuty.Step(stepped,0.1);
            Assert(stepped==35.7&&ManualDuty.Step(30,-.1)==30&&ManualDuty.Step(100,.1)==100,"stable bounded step");
        });
        Check("custom temperature points interpolate and retain all thermal safeguards",()=>
        {
            List<FanCurvePoint> points=[new(30,35),new(60,65),new(85,100)];
            Assert(FanCurve.ValidateCustom(points,out _)&&FanCurve.Evaluate("사용자 곡선",35,20,custom:points)==35,"cold first point");
            Assert(FanCurve.Evaluate("사용자 곡선",35,45,custom:points)==50&&FanCurve.Evaluate("사용자 곡선",35,75,custom:points)==86,"between configured points");
            foreach(var temp in new[]{0f,float.NaN,float.PositiveInfinity,85f,100f})Assert(FanCurve.Evaluate("사용자 곡선",35,temp,custom:points)==100,"thermal override");
            Assert(FanCurve.Evaluate("사용자 곡선",35,25,45,90,points)==45&&FanCurve.Evaluate("사용자 곡선",35,85,45,90,points)==90,"controller bounds");
        });
        Check("invalid custom curves cannot create unsafe temperature ranges",()=>
        {
            var invalid=new List<FanCurvePoint>[] {[],[new(85,100)],[new(30,35),new(30,50),new(85,100)],
                [new(30,60),new(60,40),new(85,100)],[new(30,29),new(85,100)],[new(30,35),new(84,100)],
                [new(30,35),new(85,90)],[new(float.NaN,35),new(85,100)],[new(30,float.PositiveInfinity),new(85,100)]};
            foreach(var points in invalid){Assert(!FanCurve.ValidateCustom(points,out var error)&&error.Length>0,"reject malformed curve");Assert(FanCurve.Evaluate("사용자 곡선",35,45,custom:points)==100,"invalid curve falls back to maximum");}
        });
        Check("per-channel custom curves survive worker JSON and saved profile restart",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
            try
            {
                var snapshot=new Snapshot(true,"Board",[],[new("fan1","Fan1","Controller",30,100,35,"기본 제어"),new("fan2","Fan2","Controller",30,100,40,"기본 제어")],"",DateTime.UtcNow);
                List<FanCurvePoint> points=[new(30,35.5f),new(55,55),new(85,100)];
                var store=new FanProfileStore(path);store.Save(FanProfile.Capture(snapshot,new Dictionary<string,FanPolicy>{{"fan1",new("사용자 곡선",35,points)},{"fan2",new("수동",40.5f)}}));
                var commands=new FanProfileStore(path).Load()!.Commands(snapshot);var command=JsonSerializer.Deserialize<WorkerCommand>(JsonSerializer.Serialize(commands[0]))!;
                Assert(command.Mode=="사용자 곡선"&&command.Curve!.SequenceEqual(points)&&commands[1].Curve==null&&commands[1].Value==40.5f,"distinct channel policies survive restart");
                Assert(FanCurve.Evaluate(command.Mode,command.Value,55,custom:command.Curve)==55,"restored curve drives the worker evaluator");
                var invalid=store.Load()!;invalid.Fans[0].Curve![1]=new(30,55);bool rejected=false;try{invalid.Commands(snapshot);}catch(InvalidDataException){rejected=true;}Assert(rejected,"malformed persisted curve rejected before command send");
            }
            finally{File.Delete(path);File.Delete(path+".tmp");}
        });
        Check("RGB profiles persist per-device settings, zone precedence and fresh device indices",()=>
        {
            var device=OpenRgbClient.Parse(0,DeviceBytes(perLed:true,splitZones:true));
            var baseline=SavedRgbSetting.Capture(device,"all","Static","#123456",70,new());
            var zone=SavedRgbSetting.Capture(device,"zone:1","Static","#FF5900",60,new(100,70,100));
            var other=device with{Name="Second",Serial="second"};var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
            try
            {
                var profile=RgbProfiles.Empty.Upsert(device,baseline).Upsert(device,zone).Upsert(other,baseline);
                var store=new RgbProfileStore(path);store.Save(profile);var restored=store.Load();
                Assert(restored.Devices.Count==2&&restored.Devices[0].Settings.Count==2&&restored.Devices[0].Settings[1].Color=="#FF5900","per-device order survives restart");
                Assert(restored.Devices[0].Resolve([device with{Index=7}]).Index==7,"device indices may reorder");
                Assert(restored.Upsert(device,baseline).Devices.Single(d=>d.Identity==device.Identity).Settings.Count==1,"whole device supersedes zone overrides");
                store.Save(profile with{Machine="another-pc"});bool rejected=false;try{store.Load();}catch(InvalidDataException){rejected=true;}Assert(rejected,"different PC");
            }
            finally{File.Delete(path);File.Delete(path+".tmp");}
        });
        Check("restarted RGB profile sends saved calibrated color and brightness over TCP",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true);using var client=new OpenRgbClient(fake.Port);
            var device=client.Devices()[0];var setting=SavedRgbSetting.Capture(device,"zone:1","Static","#FFA500",70,new(100,70,100));
            var profile=JsonSerializer.Deserialize<SavedRgbDevice>(JsonSerializer.Serialize(new SavedRgbDevice(device.Identity,device.Name,[setting])))!;
            profile.Apply(client);fake.Finish();var packet=fake.ZonePacket!;
            Assert(BitConverter.ToUInt32(packet,4)==1&&BitConverter.ToUInt32(packet,10)==(uint)(178|(81<<8)),"same intended color, calibration and brightness after restart");
        });
        Check("RGB restore preflight prevents all writes for stale zones or unrelated hardware",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];
            var setting=SavedRgbSetting.Capture(device,"zone:1","Static","#FF0000",70,new());
            var invalid=setting with{Zones=[new(1,"replaced header",1,1)]};bool rejected=false;
            try{new SavedRgbDevice(device.Identity,device.Name,[setting,invalid]).Apply(client);}catch(InvalidDataException){rejected=true;}
            Assert(rejected&&fake.ModePacket==null&&fake.ZonePacket==null,"validate entire restore before writing");
            rejected=false;try{new SavedRgbDevice("different identity","other",[setting]).Resolve([device]);}catch(IOException){rejected=true;}Assert(rejected,"no fallback by device name");
        });
        Check("RGB startup retries delayed detection, confirms once and respects user overrides",()=>
        {
            var now=DateTime.UtcNow;var device=new SavedRgbDevice("gpu","GPU",[]);var profile=new RgbProfiles(1,Environment.MachineName,[device]);
            var queue=new RgbRestoreQueue(profile,now);Assert(queue.Due(now)==device,"initial restore");
            queue.Failed("gpu",now,"not detected");Assert(queue.Due(now.AddSeconds(4))==null&&queue.Due(now.AddSeconds(5))==device,"retry late device");
            queue.Succeeded("gpu",now.AddSeconds(5));Assert(queue.Due(now.AddSeconds(14))==null&&queue.Due(now.AddSeconds(15))==device,"delayed GPU initialization pass");
            queue.Succeeded("gpu",now.AddSeconds(15));Assert(queue.PendingCount==0&&queue.CompletedCount==1,"bounded completion");
            queue=new(profile,now);queue.Succeeded("gpu",now);queue.Cancel("gpu");Assert(queue.Due(now.AddSeconds(11))==null,"manual color cancels obsolete startup retry");
            queue=new(profile,now);Assert(queue.Due(now.AddMinutes(2))==null&&queue.CompletedCount==0&&queue.Status.Contains("시간 초과"),"unavailable device not reported restored");
        });
        Check("SDK3 fixture parses device, mode and LEDs", () =>
        {
            var device = OpenRgbClient.Parse(0, DeviceBytes());
            Assert(device.Name == "Fixture RGB" && device.Leds == 2 && device.Modes.Single().Colors == 1 && device.Modes[0].Flags == 0x40, "fixture");
        });
        Check("truncated and oversized controller data rejected", () =>
        {
            bool rejected = false; try { OpenRgbClient.Parse(0, DeviceBytes()[..^1]); } catch (IOException) { rejected = true; }
            Assert(rejected, "truncated");
        });
        Check("fragmented real TCP frames, mode colors, brightness and readback", () =>
        {
            using var fake = new FakeServer();
            using var client = new OpenRgbClient(fake.Port);
            var devices = client.Devices(); Assert(devices.Count == 1, "count");
            client.Apply(devices[0], "Static", Color.FromRgb(100, 200, 60), 50);
            fake.Finish();
            var packet = fake.ModePacket ?? throw new Exception("missing mode write");
            using var reader = new BinaryReader(new MemoryStream(packet));
            Assert(reader.ReadUInt32() == packet.Length && reader.ReadInt32() == 0, "mode header");
            int str = reader.ReadUInt16(); reader.ReadBytes(str);
            var fields = Enumerable.Range(0, 12).Select(_ => reader.ReadUInt32()).ToArray();
            Assert(fields[11] == 2, "mode-specific enum must equal 2");
            Assert(reader.ReadUInt16() == 1 && reader.ReadUInt32() == (uint)(50 | (100 << 8) | (30 << 16)), "scaled RGB");
        });
        Check("device identity change causes zero RGB writes", () =>
        {
            using var fake = new FakeServer(changeIdentity: true);
            using var client = new OpenRgbClient(fake.Port);
            var device = client.Devices()[0]; bool rejected = false;
            try { client.Apply(device, "Static", Colors.Red, 100); } catch (IOException) { rejected = true; }
            Assert(rejected && fake.ModePacket == null, "stale device");
        });
        Check("case-fan zones retain names, LED offsets and ARGB grouping",()=>
        {
            var device=OpenRgbClient.Parse(0,DeviceBytes(perLed:true,splitZones:true));
            Assert(device.HasCaseFanHeaders&&device.Zones!.Count==2&&device.Zones[1].Start==1,"zone metadata");
            var argb=RgbZoneChoice.For(device).Single(c=>c.Key=="argb");Assert(argb.ZoneIds!.SequenceEqual(new uint[]{0,1}),"ARGB selection");
        });
        Check("zone SDK packet updates only the requested header",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true);using var client=new OpenRgbClient(fake.Port);
            var device=client.Devices()[0];var readback=client.Apply(device,"Static",Color.FromRgb(0xaa,0xbb,0xcc),100,[1]);fake.Finish();
            var body=fake.ZonePacket!;Assert(BitConverter.ToUInt32(body)==14&&BitConverter.ToUInt32(body,4)==1&&BitConverter.ToUInt16(body,8)==1,"zone packet layout");
            Assert(readback.CurrentColors![0]==0x332211&&readback.CurrentColors[1]==0xccbbaa,"other header must be preserved");
        });
        Check("invalid header is rejected before any mode or LED write",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];bool rejected=false;
            try{client.Apply(device,"Static",Colors.Red,70,[9]);}catch(IOException){rejected=true;}
            Assert(rejected&&fake.ModePacket==null&&fake.ZonePacket==null,"invalid zone must not write");
        });
        Check("zero brightness on one header leaves other header lit",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];
            var readback=client.Apply(device,"Static",Colors.Red,0,[1]);Assert(readback.CurrentColors![0]==0x332211&&readback.CurrentColors[1]==0,"header off");
        });
        Check("native effect without color support sends MODE_COLORS_NONE",()=>
        {
            using var fake=new FakeServer(flagsOverride:0);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];client.Apply(device,"Static",Colors.Red,70);fake.Finish();
            var mode=fake.ModePacket!;int nameLength=BitConverter.ToUInt16(mode,8);int fields=10+nameLength;
            Assert(BitConverter.ToUInt32(mode,fields+44)==0,"native color mode enum");
        });
        Check("native effect without per-LED colors cannot target one header",()=>
        {
            using var fake=new FakeServer(splitZones:true,flagsOverride:0);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];bool rejected=false;
            try{client.Apply(device,"Static",Colors.Red,70,[0]);}catch(InvalidDataException){rejected=true;}
            Assert(rejected&&fake.ModePacket==null,"unsupported zone mode");
        });
        Check("whole-device RGB mode rejects partial headers before any write",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true,flagsOverride:0x420);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];bool rejected=false;
            try{client.Apply(device,"Static",Colors.Red,70,[1]);}catch(InvalidDataException){rejected=true;}
            Assert(rejected&&fake.ModePacket==null&&fake.LedPacket==null&&fake.ZonePacket==null,"entire-device flag must be respected");
        });
        Check("native random effects retain automatic color mode and do not get LED writes",()=>
        {
            var native=ModeFixture("Random wave",0x881,(2,1),(3,10),(8,4),(10,0),(11,3));
            using var fake=new FakeServer(initialModeRaw:native.Raw);using var client=new OpenRgbClient(fake.Port);var result=client.Apply(client.Devices()[0],native.Name,Colors.Red,70);
            Assert(result.Modes[0].Field(11)==3&&result.Modes[0].Field(10)==6&&fake.LedPacket==null,"native random and diagonal direction");
            Assert(result.CurrentColors!.SequenceEqual(new uint[]{0x332211,0x665544}),"automatic colors are not replaced by static LED vectors");
        });
        Check("RGB dual-color capability preserves per-LED mode instead of forcing mode-specific",()=>
        {
            var dual=ModeFixture("Dual",0x60,(6,1),(7,3),(11,1));using var fake=new FakeServer(initialModeRaw:dual.Raw);using var client=new OpenRgbClient(fake.Port);
            var result=client.Apply(client.Devices()[0],dual.Name,Colors.Red,100);
            Assert(result.Modes[0].Field(11)==1&&fake.LedPacket!=null&&result.CurrentColors!.All(c=>c==255),"per-LED selection retained");
        });
        Check("mode-specific effect colors do not also send unrelated per-LED vectors",()=>
        {
            var specific=ModeFixture("Specific",0x60,(6,1),(7,3),(11,2));using var fake=new FakeServer(initialModeRaw:specific.Raw);using var client=new OpenRgbClient(fake.Port);
            var result=client.Apply(client.Devices()[0],specific.Name,Colors.Blue,100);
            Assert(result.Modes[0].Colors==1&&result.Modes[0].Field(11)==2&&fake.LedPacket==null&&result.CurrentColors![0]==0x332211,"mode colors only");
        });
        Check("native multicolor palettes are not flattened into a frozen-looking solid color",()=>
        {
            var mode=ModeFixture("Palette wave",0x40,(6,3),(7,3),(11,2));var raw=mode.Raw.Concat(new byte[12]).ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(mode.FieldsOffset+48),3);
            for(int i=0;i<3;i++)System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(mode.FieldsOffset+50+i*4),new uint[]{0x0000ff,0xff0000,0x00ff00}[i]);
            mode=mode with{Raw=raw,Colors=3};using var fake=new FakeServer(initialModeRaw:raw);using var client=new OpenRgbClient(fake.Port);
            var result=client.Apply(client.Devices()[0],mode.Name,Colors.White,100);var applied=result.Modes[0];
            Assert(BitConverter.ToUInt32(applied.Raw,applied.FieldsOffset+50)==0xffffff&&BitConverter.ToUInt32(applied.Raw,applied.FieldsOffset+54)==0xff0000&&BitConverter.ToUInt32(applied.Raw,applied.FieldsOffset+58)==0x00ff00,"only requested first palette entry changes");
        });
        Check("RGB speed stays in native bounds, preserves valid zero and fixes invalid defaults",()=>
        {
            var speed=ModeFixture("Wave",1,(2,10),(3,90),(8,0));
            Assert(RgbWritePlan.Build(speed,Colors.White,100,false).Speed==50,"invalid inactive speed becomes a valid midpoint");
            Assert(RgbWritePlan.Build(speed,Colors.White,100,false,75).Speed==70,"explicit percentage uses native bounds");
            var zero=ModeFixture("Wave",1,(2,0),(3,3),(8,0));Assert(RgbWritePlan.Build(zero,Colors.White,100,false).Speed==0,"zero may be a valid device-specific speed");
            var binary=ModeFixture("Binary speed",0x11,(2,0),(3,1),(4,0),(5,1));var binaryPlan=RgbWritePlan.Build(binary,Colors.White,50,false,50);
            Assert(binaryPlan.Speed==1&&binaryPlan.Brightness==1,"midpoint must not accidentally round a binary speed or brightness down to off");
            foreach(var pair in new[]{(Flags:4u,Direction:2u),(Flags:8u,Direction:4u),(Flags:0x800u,Direction:6u)})
            {
                var mode=ModeFixture("Directional",pair.Flags,(10,99));var plan=RgbWritePlan.Build(mode,Colors.White,100,false);
                Assert(BitConverter.ToUInt32(plan.Raw,mode.FieldsOffset+40)==pair.Direction,"supported direction enum");
            }
        });
        Check("malformed RGB speed and brightness are rejected without changing the mode",()=>
        {
            foreach(var mode in new[]{ModeFixture("Invalid speed",1,(2,10),(3,1)),ModeFixture("Invalid brightness",0x10,(4,100),(5,0)),ModeFixture("Invalid palette",0x40,(6,1),(7,1025),(11,2))})
            {
                using var fake=new FakeServer(initialModeRaw:mode.Raw);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];bool rejected=false;
                try{client.Apply(device,mode.Name,Colors.Red,70);}catch(InvalidDataException){rejected=true;}
                Assert(rejected&&fake.ModePacket==null,"preflight before mode mutation");
            }
        });
        Check("zero brightness selects Off without carrying an animated effect speed",()=>
        {
            var off=ModeFixture("Off",0);
            using var fake=new FakeServer(initialModeRaw:off.Raw);using var client=new OpenRgbClient(fake.Port);
            var applied=client.Apply(client.Devices()[0],"Wave",Colors.Red,0,speedPercent:75);
            Assert(applied.Modes[applied.ActiveMode].Name=="Off"&&fake.LedPacket==null,"Off ignores animation speed and does not send LED colors");
            var saved=SavedRgbSetting.Capture(applied,"all","Off","#FF0000",0,new());
            Assert(saved.SpeedPercent==null,"saved Off effect has no animation speed");
        });
        Check("RGB saved effect speed survives restart and reaches the SDK packet",()=>
        {
            var mode=ModeFixture("Wave",0x21,(2,10),(3,90),(8,50));using var fake=new FakeServer(initialModeRaw:mode.Raw);using var client=new OpenRgbClient(fake.Port);var device=client.Devices()[0];
            var setting=SavedRgbSetting.Capture(device,"all",mode.Name,"#112233",70,new(),75);
            var stored=JsonSerializer.Deserialize<SavedRgbDevice>(JsonSerializer.Serialize(new SavedRgbDevice(device.Identity,device.Name,[setting])))!;
            stored.Apply(client);Assert(stored.Settings[0].SpeedPercent==75&&BitConverter.ToUInt32(fake.ModePacket!,8+mode.FieldsOffset+32)==70,"persisted speed");
        });
        Check("RGB engine ignoring an effect request is not reported as accepted",()=>
        {
            var mode=ModeFixture("Wave",1,(2,10),(3,90),(8,50));using var fake=new FakeServer(initialModeRaw:mode.Raw,ignoreModeWrites:true);using var client=new OpenRgbClient(fake.Port);bool rejected=false;
            try{client.Apply(client.Devices()[0],mode.Name,Colors.Red,70,speedPercent:75);}catch(IOException){rejected=true;}
            Assert(rejected,"speed readback mismatch");
        });
        Check("stalled RGB replies have a bounded deadline and never write a mode",()=>
        {
            using var fake=new FakeServer(stallDeviceResponse:true);var watch=System.Diagnostics.Stopwatch.StartNew();bool failed=false;
            try{using var client=new OpenRgbClient(fake.Port,TimeSpan.FromMilliseconds(600));client.Devices();}catch(Exception ex)when(ex is IOException or TimeoutException){failed=true;}
            Assert(failed&&watch.Elapsed<TimeSpan.FromSeconds(3)&&fake.ModePacket==null,"bounded no-response recovery");
        });
        Check("RGB diagnostics omit private device identifiers and machine paths",()=>
        {
            var device=OpenRgbClient.Parse(0,DeviceBytes()) with{Serial="SERIAL_SECRET",Location="USB_PATH_SECRET"};var json=RgbDiagnostics.Serialize([device],"app-owned",null);
            Assert(json.Contains("Fixture RGB")&&json.Contains("SupportsColor")&&!json.Contains("SERIAL_SECRET")&&!json.Contains("USB_PATH_SECRET")&&!json.Contains(Environment.MachineName),"shareable RGB-only report");
        });
        Check("permanent RGB profile errors stop startup retries until an explicit retry",()=>
        {
            var now=DateTime.UtcNow;var queue=new RgbRestoreQueue(new(1,Environment.MachineName,[new("device","Device",[])]),now);
            queue.Failed("device",now,"unsupported effect",retry:false);
            Assert(queue.PendingCount==0&&queue.CompletedCount==0&&queue.Due(now.AddSeconds(10))==null,"no repeated bad-mode writes");
        });
        Check("explicit upgrade session restores only verified channels and duty",()=>
        {
            var snapshot=new Snapshot(true,"Board",[],[new("fan-1","Fan1","Board",30,100,30,"기본 제어")],"",DateTime.UtcNow);
            var session=new FanSession("Board",DateTime.UtcNow,[new("fan-1","수동",30)]);var commands=session.Commands(snapshot);
            Assert(commands.Length==1&&commands[0].Id=="fan-1"&&commands[0].Value==30,"handover");
            bool rejected=false;try{(session with{Board="different"}).Commands(snapshot);}catch(InvalidDataException){rejected=true;}Assert(rejected,"different board");
            rejected=false;try{(session with{CreatedUtc=DateTime.UtcNow.AddHours(-1)}).Commands(snapshot);}catch(InvalidDataException){rejected=true;}Assert(rejected,"expired session");
        });
        Check("invalid upgrade duty cannot issue a fan command",()=>
        {
            var snapshot=new Snapshot(true,"Board",[],[new("fan-1","Fan1","Board",30,100,30,"기본 제어")],"",DateTime.UtcNow);bool rejected=false;
            try{new FanSession("Board",DateTime.UtcNow,[new("fan-1","수동",float.NaN)]).Commands(snapshot);}catch(InvalidDataException){rejected=true;}Assert(rejected,"invalid duty");
        });
        Check("durable fan profile retains exact per-channel requests across restart and thermal override",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");var store=new FanProfileStore(path);
            try
            {
                var snapshot=new Snapshot(true,"Board",[],[new("fan1","Fan1","Controller",30,100,29.8f,"수동"),new("fan2","Fan2","Controller",30,100,70,"균형")],"",DateTime.UtcNow);
                var desired=new Dictionary<string,FanPolicy>{{"fan1",new("수동",30)},{"fan2",new("균형",65)}};
                store.Save(FanProfile.Capture(snapshot,desired));desired.Clear();
                var commands=new FanProfileStore(path).Load()!.Commands(snapshot);
                Assert(commands.Length==2&&commands[0].Value==30&&commands[1].Mode=="균형"&&commands[1].Value==65,"distinct requested values survive physical control release");
                Assert(FanCurve.Evaluate(commands[0].Mode,commands[0].Value,90)==100&&store.Load()!.Fans[0].Value==30,"temporary overheat must not overwrite desired duty");
                store.Clear();Assert(new FanProfileStore(path).Load()==null,"explicit restore must prevent next launch restoring");
            }
            finally{File.Delete(path);File.Delete(path+".tmp");}
        });
        Check("saved fan settings reject changed hardware, bounds, duplicates and corrupt files",()=>
        {
            var snapshot=new Snapshot(true,"Board",[],[new("fan1","Fan1","Controller",30,100,30,"기본 제어")],"",DateTime.UtcNow);
            var valid=new FanProfile(1,Environment.MachineName,"Board",[new("fan1","Controller","수동",30)]);
            foreach(var invalid in new[]{valid with{Board="Other"},valid with{Machine="Other"},valid with{Fans=[new("fan1","Other","수동",30)]},valid with{Fans=[new("fan1","Controller","수동",float.NaN)]},valid with{Fans=[new("fan1","Controller","수동",29)]},valid with{Fans=[valid.Fans[0],valid.Fans[0]]},valid with{Fans=[new("missing","Controller","수동",30)]}})
            {bool rejected=false;try{invalid.Commands(snapshot);}catch(InvalidDataException){rejected=true;}Assert(rejected,"invalid persisted profile");}
            var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");try{File.WriteAllText(path,"{corrupt");bool rejected=false;try{new FanProfileStore(path).Load();}catch(JsonException){rejected=true;}Assert(rejected,"corrupt settings");}finally{File.Delete(path);}
        });
        Check("ASRock USB header Direct requests use native Static and preserve other headers",()=>
        {
            using var fake=new FakeServer(perLed:true,splitZones:true,asrock:true);using var client=new OpenRgbClient(fake.Port);
            var device=client.Devices()[0];var result=client.Apply(device,"Direct",Colors.Red,100,[1]);fake.Finish();
            Assert(result.Modes[result.ActiveMode].Name=="Static"&&result.CurrentColors![0]==0x332211&&result.CurrentColors[1]==255,"native Static path and header preservation");
        });
        Check("LED calibration reduces green without changing input hue code or double scaling brightness",()=>
        {
            var original=Color.FromRgb(255,165,0);var adjusted=new RgbCalibration(100,80,100).Apply(original);
            Assert(adjusted==Color.FromRgb(255,132,0)&&original==Color.FromRgb(255,165,0),"green correction");
            Assert(new RgbCalibration().Apply(original)==original&&new RgbCalibration(200,200,200).Apply(Colors.White)==Colors.White,"neutral and saturation");
            using var fake=new FakeServer();using var client=new OpenRgbClient(fake.Port);client.Apply(client.Devices()[0],"Static",adjusted,70);fake.Finish();
            var packet=fake.ModePacket!;Assert(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packet.Length-4))==(uint)(178|(92<<8)),"one brightness scale and RGB channel order");
        });
        Check("PA120 physical mapping is a complete permutation of 84 LEDs",()=>Assert(Pa120Protocol.WireToLogical.Length==84&&Pa120Protocol.WireToLogical.Distinct().Count()==84&&Pa120Protocol.WireToLogical.Min()==0&&Pa120Protocol.WireToLogical.Max()==83,"mapping"));
        Check("PA120 initialization matches TRCC 2.1.8.2 and rejects unrelated replies",()=>
        {
            var request=Pa120Protocol.InitializationReport();Assert(request.Length==65&&request[0]==0&&request[1]==0xda&&request[4]==0xdd&&request[13]==1&&request.Skip(14).All(x=>x==0),"init report");
            var reply=request.ToArray();reply[6]=16;Assert(Pa120Protocol.IsPa120Reply(reply,65),"PA120 PM");reply[6]=32;Assert(!Pa120Protocol.IsPa120Reply(reply,65),"AK120 must be rejected");reply[6]=16;reply[13]=2;Assert(!Pa120Protocol.IsPa120Reply(reply,65),"data packet is not a handshake");
        });
        Check("PA120 42C, 100 percent and missing data have distinct numeric masks",()=>
        {
            var settings=new DisplayOptions(Colors.White,100,false,true,true,true,true);
            var metrics=new DisplayMetrics(42,100,null,0,DateTime.UtcNow);
            var mask=Pa120Protocol.Mask(metrics,settings);
            Assert(mask.Skip(10).Take(7).All(x=>!x),"temperature leading blank");
            Assert(mask.Skip(17).Take(7).Select((v,i)=>v==((0x66&(1<<i))!=0)).All(x=>x),"digit 4");
            Assert(mask.Skip(24).Take(7).Select((v,i)=>v==((0x5b&(1<<i))!=0)).All(x=>x),"digit 2");
            Assert(mask[80]&&mask[81]&&mask.Skip(31).Take(7).Count(x=>x)==6&&mask.Skip(38).Take(7).Count(x=>x)==6,"100 percent must contain both zero digits");
            Assert(mask.Skip(45).Take(21).Count(x=>x)==3&&mask[51]&&mask[58]&&mask[65],"missing temperature dashes");
        });
        Check("PA120 HID reports carry report ID, exact header, scaling and zero padding",()=>
        {
            var options=new DisplayOptions(Colors.White,100,false,true,false,false,false);
            var frame=Pa120Protocol.Frame(new(42,null,null,null,DateTime.UtcNow),options);
            Assert(frame.Length==272&&frame[0]==0xda&&frame[3]==0xdd&&frame[12]==2&&frame[16]==252&&frame[17]==0,"header");
            Assert(frame.Skip(20).Max()==102,"0.4 hardware scale");
            var reports=Pa120Protocol.Reports(frame);
            Assert(reports.Length==5&&reports.All(r=>r.Length==65&&r[0]==0),"report count and IDs");
            Assert(reports.SelectMany(r=>r.Skip(1)).Take(272).SequenceEqual(frame)&&reports[4].Skip(17).All(b=>b==0),"chunk reconstruction and padding");
        });
        Check("PA120 disabled metrics and zero brightness send no LEDs",()=>
        {
            var m=new DisplayMetrics(42,100,50,80,DateTime.UtcNow);
            Assert(Pa120Protocol.Frame(m,new(Colors.White,100,false,false,false,false,false)).Skip(20).All(x=>x==0),"disabled");
            Assert(Pa120Protocol.Frame(m,new(Colors.White,0,false,true,true,true,true)).Skip(20).All(x=>x==0),"brightness zero");
        });
        Check("display telemetry selects CPU package and discrete GPU core, not hotspot",()=>
        {
            var s=new Snapshot(true,"Test",[new("/amdcpu/0/temperature/0","Core (Tctl/Tdie)","CPU","Temperature",42),new("/amdcpu/0/load/0","CPU Total","CPU","Load",12),new("/gpu-nvidia/0/temperature/0","GPU Core","GPU","Temperature",50),new("/gpu-nvidia/0/temperature/1","GPU Hot Spot","GPU","Temperature",70),new("/gpu-amd/0/temperature/0","GPU Core","iGPU","Temperature",60),new("/gpu-nvidia/0/load/0","GPU Core","GPU","Load",35)],[],"",DateTime.UtcNow);
            var m=DisplayMetrics.From(s);Assert(m.CpuTemperature==42&&m.CpuLoad==12&&m.GpuTemperature==50&&m.GpuLoad==35,"sensors");
        });
        Check("v1.0 preferences remain readable with v1.1 display defaults",()=>
        {
            var prefs=JsonSerializer.Deserialize<Preferences>("{\"Color\":\"#123456\",\"Brightness\":50,\"FanMode\":\"수동\",\"Manual\":65}")!;
            Assert(prefs.Color=="#123456"&&prefs.Manual==65&&prefs.DisplayBrightness==70&&prefs.CpuTemp,"migration");
        });
        if (args.Contains("--with-engine")) Check("bundled OpenRGB 1.1 SDK negotiation and zero-device response (--nodetect)", () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string path = Path.Combine(AppContext.BaseDirectory, "tools", "OpenRGB", "OpenRGB.exe");
            var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)! };
            foreach (string arg in new[] { "--nodetect", "--noautoconnect", "--server", "--server-host", "127.0.0.1", "--server-port", port.ToString(), "--config", Path.Combine(AppFiles.Data, "sdk-test") }) start.ArgumentList.Add(arg);
            Directory.CreateDirectory(Path.Combine(AppFiles.Data, "sdk-test"));
            using var process = System.Diagnostics.Process.Start(start)!;
            try
            {
                Exception? error = null; bool connected = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { using var client = new OpenRgbClient(port); Assert(client.Devices().Count == 0, "nodetect should be empty"); connected = true; break; }
                    catch (Exception ex) { error = ex; Thread.Sleep(300); }
                }
                if (!connected) throw error ?? new IOException("SDK did not start");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
        });
        lines.Add($"{passed} passed; {failed} failed. Hardware writes were not performed by these tests.");
        var at = Array.IndexOf(args, "--output");
        if (at >= 0 && at + 1 < args.Length) File.WriteAllLines(args[at + 1], lines);
        else using (var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }) foreach (var line in lines) output.WriteLine(line);
        return failed == 0 ? 0 : 1;
    }
    private static void String(BinaryWriter w, string s) { var bytes = Encoding.UTF8.GetBytes(s + "\0"); w.Write((ushort)bytes.Length); w.Write(bytes); }
    internal static RgbMode ModeFixture(string name,uint flags,params (int Field,uint Value)[] values)
    {
        var mode=OpenRgbClient.Parse(0,DeviceBytes(perLed:true,flagsOverride:flags)).Modes[0];
        foreach(var value in values)System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mode.Raw.AsSpan(mode.FieldsOffset+value.Field*4),value.Value);
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer);String(writer,name);int offset=(int)buffer.Position;writer.Write(mode.Raw[mode.FieldsOffset..]);
        return mode with{Name=name,Raw=buffer.ToArray(),FieldsOffset=offset};
    }
    private static byte[] DeviceBytes(string serial = "fixture-1",bool perLed=false,bool splitZones=false,uint[]? colors=null,uint? flagsOverride=null,bool asrock=false,byte[]? modeRaw=null)
    {
        using var buffer = new MemoryStream(); using var w = new BinaryWriter(buffer);
        w.Write(0u); w.Write(0); String(w, asrock?"ASRock Test Board":"Fixture RGB"); String(w, "Test vendor"); String(w, "Test device"); String(w, "1.0"); String(w, serial); String(w, asrock?"HID:test":"USB:test");
        w.Write((ushort)1); w.Write(0);
        if(modeRaw!=null)w.Write(modeRaw);
        else
        {
            String(w, "Static");
            foreach (uint field in new uint[] { 0, flagsOverride??(perLed?0x20u:0x40u), 0, 0, 0, 100, perLed?0u:1u, perLed?0u:1u, 0, 100, 0, perLed?1u:2u }) w.Write(field);
            w.Write((ushort)(perLed?0:1));if(!perLed)w.Write(0u);
        }
        w.Write((ushort)(splitZones?2:1));
        String(w,"Addressable Header 1");w.Write(1);w.Write(splitZones?1u:2u);w.Write(splitZones?1u:2u);w.Write(splitZones?1u:2u);w.Write((ushort)0);
        if(splitZones){String(w,"Addressable Header 2");w.Write(1);w.Write(1u);w.Write(1u);w.Write(1u);w.Write((ushort)0);}
        w.Write((ushort)2); String(w, "LED1"); w.Write(0u); String(w, "LED2"); w.Write(1u);
        w.Write((ushort)2);w.Write(colors?[0]??0);w.Write(colors?[1]??0);w.Flush();
        buffer.Position = 0; w.Write((uint)buffer.Length); return buffer.ToArray();
    }
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task task;
        private TcpClient? connection;
        public int Port { get; }
        public byte[]? ModePacket { get; private set; }
        public byte[]? ZonePacket{get;private set;}
        public byte[]? LedPacket{get;private set;}
        public FakeServer(bool changeIdentity = false,bool perLed=false,bool splitZones=false,uint? flagsOverride=null,bool asrock=false,byte[]? initialModeRaw=null,bool stallDeviceResponse=false,bool ignoreModeWrites=false)
        {
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            task = Task.Run(() =>
            {
                try
                {
                    connection = listener.AcceptTcpClient(); using var stream = connection.GetStream(); stream.ReadTimeout = 6000;
                    int reads = 0;uint[] colors=[0x332211,0x665544];byte[]? modeRaw=initialModeRaw;
                    while (true)
                    {
                        byte[] header = new byte[16]; stream.ReadExactly(header); uint id = BitConverter.ToUInt32(header, 8), device = BitConverter.ToUInt32(header, 4);
                        byte[] body = new byte[BitConverter.ToUInt32(header, 12)]; stream.ReadExactly(body);
                        byte[]? answer = id switch { 40 => BitConverter.GetBytes(6u), 0 => BitConverter.GetBytes(1u), 1 when !stallDeviceResponse => DeviceBytes(changeIdentity && ++reads > 1 ? "changed" : "fixture-1",perLed,splitZones,colors,flagsOverride,asrock,modeRaw), _ => null };
                        if (id == 1101){ModePacket = body;if(!ignoreModeWrites)modeRaw=body[8..];}
                        if(id==1051){ZonePacket=body;int zone=(int)BitConverter.ToUInt32(body,4);int count=BitConverter.ToUInt16(body,8);int start=splitZones?zone:0;for(int n=0;n<count;n++)colors[start+n]=BitConverter.ToUInt32(body,10+n*4);}
                        if(id==1050){LedPacket=body;int count=BitConverter.ToUInt16(body,4);for(int n=0;n<count;n++)colors[n]=BitConverter.ToUInt32(body,6+n*4);}
                        if (answer == null) continue;
                        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(Encoding.ASCII.GetBytes("ORGB")); w.Write(device); w.Write(id); w.Write((uint)answer.Length); w.Write(answer);
                        var bytes = output.ToArray(); stream.Write(bytes.AsSpan(0, 7)); stream.Write(bytes.AsSpan(7));
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            });
        }
        public void Finish() { connection?.Close(); task.Wait(2000); }
        public void Dispose() { connection?.Dispose(); listener.Stop(); task.Wait(2000); }
    }
}
