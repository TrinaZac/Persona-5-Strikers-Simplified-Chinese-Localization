using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public class PatchFile {
    public string Path, Delta, DeltaSHA256, BeforeSHA256, AfterSHA256;
    public long BeforeBytes, AfterBytes, DeltaBytes;
    public int Records;
}
public class Guard {
    public string Path;
    public long Offset;
    public byte[] Before, After;
}
public class Component {
    public string Path, Payload, SHA256, SharedWith;
    public bool Shared;
}
public class Manifest {
    public int Format;
    public string ModId, Version, SteamBuildId;
    public PatchFile[] Files;
    public Guard[] Guards;
    public Component[] Components;
}
public class TransactionFile {
    public string Path, BeforeHash, AfterHash;
    public bool Existed;
}
public class State {
    public string ModId, Version, ManifestSHA256, Status, Time;
    public TransactionFile[] Transaction;
    public Dictionary<string,long> OriginalLengths;
    public Dictionary<string,bool> ManagedPaths;
}
public class ComponentChange {
    public Component Component;
    public string BeforeHash, AfterHash;
    public bool Owned;
}
public class Region {
    public byte Kind;
    public long Offset;
    public byte[] Before, After;
}

public static class P5SPatch {
    const string ExpectedManifest="d272dc3a267d98ba0d6a8228da31177f8276b4bd7031bc8dfde209fb1ad75ffe";
    const string StateFolder=".p5s-simplified-chinese";
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static readonly Dictionary<string,List<Region>> Regions=new Dictionary<string,List<Region>>();
    static string Package, Root, Store;
    static Manifest M;
    static bool Yes, CrashAfter, PrerequisitesReady=true;
    static int FailAfter;
    static State Active;

    [STAThread]
    public static int Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);
        Console.WriteLine("P5S 简体中文补丁 1.1.0");
        try {
            Package=AppDomain.CurrentDomain.BaseDirectory;
            string action=args.Length==0?"help":args[0].ToLowerInvariant();
            if(action=="help" || action=="--help") {
                Console.WriteLine("Install.cmd 安装，Uninstall.cmd 卸载。游戏语言请选择繁体中文。");
                Console.WriteLine("CLI: install|check|repair|uninstall|recover --game <folder> [--yes]");
                return 0;
            }
            if(action=="credits" || action=="--credits") {
                using(Stream stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("P5S.Credits.txt"))
                using(StreamReader reader=new StreamReader(stream,Encoding.UTF8))Console.WriteLine(reader.ReadToEnd());
                return 0;
            }
            if(!new[] {"install","check","repair","uninstall","recover"}.Contains(action))throw new Exception("Unknown action.");
            string selected=null;
            for(int i=1;i<args.Length;i++) {
                if(args[i]=="--game" && i+1<args.Length)selected=args[++i];
                else if(args[i]=="--yes")Yes=true;
                else throw new Exception("Unknown or incomplete argument: "+args[i]);
            }
            CheckPackage(Safe(Package,"manifest.json"),ExpectedManifest);
            M=Json.Deserialize<Manifest>(File.ReadAllText(Safe(Package,"manifest.json"),Encoding.UTF8));
            if(M.Format!=4 || M.ModId!="p5s-simplified-chinese" || M.Version!="1.1.0" || M.Files.Length!=4 || M.Components==null || M.Components.Length!=4)throw new Exception("Unsupported manifest.");
            Root=Path.GetFullPath(selected??FindGame()).TrimEnd('\\','/');
            Store=Safe(Root,StateFolder);
            if(!File.Exists(Safe(Root,"game.exe")))throw new Exception("请选择含 game.exe 的 P5S 游戏目录。");
            Console.WriteLine("游戏目录 / Game: "+Root);
            string lockName="Local\\P5S_SC_"+Hex(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))).Substring(0,24);
            using(Mutex gate=new Mutex(false,lockName)) {
                bool owned=false;
                try {
                    try {owned=gate.WaitOne(0);} catch(AbandonedMutexException) {owned=true;}
                    if(!owned)throw new Exception("另一个补丁操作正在运行。");
                    RejectLinks(Root);RejectLinks(Store);
                    foreach(PatchFile p in M.Files)RejectLinks(Safe(Root,p.Path));
                    foreach(Guard g in M.Guards)RejectLinks(Safe(Root,g.Path));
                    foreach(Component c in M.Components) {
                        RejectLinks(Safe(Root,c.Path));
                        CheckPackage(Safe(Package,c.Payload),c.SHA256);
                    }
                    if(action!="check")EnsureGameClosed();
                    // Existing Steam preflight also applies to repair.
                    CheckSteamPrerequisites(action=="repair"?"install":action);
                    ConfigureFaultTest();
                    LoadRegions();
                    ValidateExecutable();
                    ValidateGuards();
                    Active=ReadState();
                    if(action=="check")return Check();
                    if(Active!=null && Active.Transaction!=null) {
                        RecoverTransaction();
                        Console.WriteLine("上次操作已恢复，请再次运行所需操作。");return 0;
                    }
                    if(action=="recover") {
                        if(Active==null || !IsPending(Active))throw new Exception("没有中断的操作可恢复。请使用 Install.cmd、repair 或 Uninstall.cmd。");
                        Change(false);return 0;
                    }
                    if(Active!=null && IsPending(Active)) {
                        Change(false);Console.WriteLine("旧版中断操作已按汉化区域恢复，请重新运行所需操作。");return 0;
                    }
                    Change(action!="uninstall");
                    return 0;
                } finally {if(owned)gate.ReleaseMutex();}
            }
        } catch(OperationCanceledException) {Console.WriteLine("已取消。");return 2;}
        catch(Exception ex) {
            Console.WriteLine("操作未完成 / FAILED: "+ex.Message);
            Console.WriteLine("请保留完整错误信息及 "+StateFolder+" 文件夹。");return 1;
        }
    }
    static string Hex(byte[] bytes) {return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
    static string Hash(string path) {
        using(FileStream stream=File.OpenRead(path))
        using(SHA256 sha=SHA256.Create())return Hex(sha.ComputeHash(stream));
    }
    static void CheckPackage(string path,string expected) {
        if(!File.Exists(path) || Hash(path)!=expected)throw new Exception("补丁安装包不完整，请重新下载并完整解压。文件 "+path);
    }
    static byte[] ReadExact(BinaryReader reader,int count) {
        byte[] bytes=reader.ReadBytes(count);
        if(bytes.Length!=count)throw new Exception("补丁数据被截断。");
        return bytes;
    }
    static byte[] ReadAt(FileStream stream,long offset,int count) {
        stream.Position=offset;byte[] bytes=new byte[count];int done=0;
        while(done<count) {int n=stream.Read(bytes,done,count-done);if(n==0)throw new Exception("资源缺失所需区域，偏移 "+offset);done+=n;}
        return bytes;
    }
    static void LoadRegions() {
        foreach(PatchFile p in M.Files) {
            string path=Safe(Package,p.Delta);CheckPackage(path,p.DeltaSHA256);
            List<Region> list=new List<Region>();
            using(FileStream stream=File.OpenRead(path))
            using(GZipStream gzip=new GZipStream(stream,CompressionMode.Decompress))
            using(BinaryReader reader=new BinaryReader(gzip)) {
                if(Encoding.ASCII.GetString(ReadExact(reader,8))!="P5SSCP4\0")throw new Exception("补丁区域格式不正确。");
                int count=reader.ReadInt32();if(count!=p.Records || count<1 || count>100000)throw new Exception("补丁区域数量不正确。");
                long previous=0;
                for(int i=0;i<count;i++) {
                    byte kind = reader.ReadByte();
                    long offset = reader.ReadInt64();
                    int before = reader.ReadInt32();
                    int after = reader.ReadInt32();
                    if (kind > 1 || offset < previous || before < 0 || after < 1 || after > 67108864 ||
                        (kind == 0 && before != after) ||
                        (kind == 1 && (before != 0 || offset != p.BeforeBytes || after != p.AfterBytes - p.BeforeBytes)))
                        throw new Exception("补丁区域边界不正确。");
                    byte[] original = ReadExact(reader, before);
                    byte[] target = ReadExact(reader, after);
                    list.Add(new Region { Kind = kind, Offset = offset, Before = original,
                                          After = target });
                    previous=offset+after;
                }
                if(gzip.ReadByte()!=-1)throw new Exception("补丁数据含意外尾部。");
            }
            Regions.Add(p.Path,list);
        }
    }
    static void ValidateExecutable() {
        using(FileStream stream=File.OpenRead(Safe(Root,"game.exe"))) {
            byte[] dos=ReadAt(stream,0,64);int offset=BitConverter.ToInt32(dos,60);
            if(dos[0]!=77 || dos[1]!=90 || offset<64 || offset>stream.Length-6)throw new Exception("game.exe 不是有效的 Windows 可执行文件。");
            byte[] pe=ReadAt(stream,offset,6);
            if(BitConverter.ToInt32(pe,0)!=0x4550 || BitConverter.ToUInt16(pe,4)!=0x8664)throw new Exception("需要 Windows x64 版 P5S。");
        }
    }
    static void Conflict(string path,long offset) {
        throw new Exception("汉化涉及的区域发生冲突 / Affected region conflict: "+path+"，偏移 0x"+offset.ToString("x")+"。请恢复该资源或通过 Steam 校验文件后重试。");
    }
    static void ValidateGuards() {
        foreach(Guard g in M.Guards) {
            string path=Safe(Root,g.Path);
            if(g.Offset<0 || g.Before.Length!=g.After.Length)throw new Exception("索引守卫格式不正确。");
            using(FileStream stream=File.OpenRead(path)) {
                if(g.Path.EndsWith(".rdb",StringComparison.OrdinalIgnoreCase) && Encoding.ASCII.GetString(ReadAt(stream,0,4))!="_DRK")throw new Exception("资源索引格式无法识别。 "+g.Path);
                byte[] current=ReadAt(stream,g.Offset,g.Before.Length);
                for(int i=0;i<current.Length;i++)if(current[i]!=g.Before[i] && current[i]!=g.After[i])Conflict(g.Path,g.Offset+i);
            }
        }
    }
    static string ResourceState(PatchFile p) {
        bool before=false,after=false;
        bool appendReferenced=true;
        if(p.AfterBytes>p.BeforeBytes && p.Path.EndsWith(".bin",StringComparison.OrdinalIgnoreCase)) {
            string indexPath=p.Path.Substring(0,p.Path.Length-4);
            PatchFile index=M.Files.FirstOrDefault(x=>x.Path==indexPath);
            if(index!=null)appendReferenced=ResourceState(index)!="Original";
        }
        using(FileStream stream=File.OpenRead(Safe(Root,p.Path))) {
            foreach(Region r in Regions[p.Path]) {
                if(r.Kind==1) {
                    if(stream.Length<=r.Offset) {before=true;continue;}
                    int available=(int)Math.Min(stream.Length-r.Offset,r.After.Length);
                    byte[] current=ReadAt(stream,r.Offset,available);
                    for(int i=0;i<available;i++)if(current[i]!=r.After[i])Conflict(p.Path,r.Offset+i);
                    if(appendReferenced)after=true;
                    if(available<r.After.Length)before=true;
                    continue;
                }
                byte[] bytes=ReadAt(stream,r.Offset,r.Before.Length);
                for(int i=0;i<bytes.Length;i++) {
                    if (r.Before[i] == r.After[i]) {
                        continue;
                    } else if (bytes[i] == r.Before[i]) before = true;
                    else if (bytes[i] == r.After[i]) after = true;
                    else Conflict(p.Path, r.Offset + i);
                }
            }
        }
        return before?(after?"Mixed":"Original"):"Patched";
    }
    static State ReadState() {
        if(!File.Exists(StatePath))return null;
        State state=Json.Deserialize<State>(File.ReadAllText(StatePath,Encoding.UTF8));
        if(state==null || state.ModId!=M.ModId)throw new Exception("安装记录无法识别，请保留原记录。");
        if(state.Version!=M.Version || state.ManifestSHA256!=ExpectedManifest) {
            if(state.Status=="Uninstalled" && state.Transaction==null)return null;
            throw new Exception("请先使用旧版汉化包的 Uninstall.cmd 卸载旧版，再运行新版 Install.cmd。");
        }
        if(!new[] {"Installed","Uninstalled","Preparing","Committing","Uninstalling","RecoveryRequired","Recovered"}.Contains(state.Status))throw new Exception("安装记录状态无法识别。");
        if(state.Transaction!=null) {
            if(state.Transaction.Length<1 || state.Transaction.Length>M.Files.Length+M.Components.Length)throw new Exception("事务记录不完整。");
            HashSet<string> paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(TransactionFile transaction in state.Transaction) {
                bool known=M.Files.Any(p=>p.Path==transaction.Path) || M.Components.Any(c=>c.Path==transaction.Path);
                if(!known || !paths.Add(transaction.Path) ||
                   (transaction.Existed && !Regex.IsMatch(transaction.BeforeHash??"","^[a-f0-9]{64}$")) ||
                   (!transaction.Existed && transaction.BeforeHash!=null) ||
                   (transaction.AfterHash!=null && !Regex.IsMatch(transaction.AfterHash,"^[a-f0-9]{64}$")))throw new Exception("事务记录无效。");
            }
        }
        if(state.ManagedPaths!=null && state.ManagedPaths.Keys.Any(path=>!M.Components.Any(c=>c.Path==path)))throw new Exception("组件安装记录含未知路径。");
        return state;
    }
    static bool IsPending(State s) {return new[] {"Preparing","Committing","Uninstalling","RecoveryRequired"}.Contains(s.Status);}
    static void SaveState(string status) {
        Active.Status=status;Active.ModId=M.ModId;Active.Version=M.Version;Active.ManifestSHA256=ExpectedManifest;Active.Time=DateTimeOffset.Now.ToString("o");
        Directory.CreateDirectory(Store);string temp=Safe(Store,"state.json.tmp");RejectLinks(temp);
        byte[] bytes=Encoding.UTF8.GetBytes(Json.Serialize(Active));
        using(FileStream stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
        if(File.Exists(StatePath))File.Replace(temp,StatePath,null);else File.Move(temp,StatePath);
    }
    static int Check() {
        string[] states=M.Files.Select(p=>ResourceState(p)).ToArray();
        for(int i=0;i<states.Length;i++)Console.WriteLine(states[i]+" : "+M.Files[i].Path);
        bool componentsReady=true;
        foreach(Component component in M.Components) {
            string current=HashIfExists(Safe(Root,component.Path));
            Console.WriteLine((current==component.SHA256?"Patched":current==null?"Original":"Conflict")+" : "+component.Path);
            if(current!=component.SHA256)componentsReady=false;
        }
        if(!PrerequisitesReady)return 3;
        if(Active!=null && (Active.Transaction!=null || IsPending(Active))) {Console.WriteLine("CHECK_PENDING：存在中断事务，请运行 recover。");return 3;}
        if(states.All(x=>x=="Patched") && componentsReady) {Console.WriteLine("CHECK_INSTALLED_OK：汉化区域和输入组件完整。");return 0;}
        if(states.All(x=>x=="Original") && !File.Exists(Safe(Root,"P5SUnicodeInput.asi"))) {Console.WriteLine("CHECK_ORIGINAL_OK：汉化区域为原版，可安装。");return 0;}
        Console.WriteLine("CHECK_MIXED：含旧版汉化或原版区域。运行 Install.cmd 或 repair 可更新，运行 Uninstall.cmd 可恢复。");return 3;
    }
    static string Stage(string path) {return Safe(Store,"scoped-transaction/stage/"+path);}
    static string Rollback(string path) {return Safe(Store,"scoped-transaction/rollback/"+path);}
    static string Stage(PatchFile p) {return Stage(p.Path);}
    static string Rollback(PatchFile p) {return Rollback(p.Path);}
    static string HashIfExists(string path) {return File.Exists(path)?Hash(path):null;}
    static void Parent(string path) {RejectLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path));}
    static void MakeStage(PatchFile p,bool install) {
        string stage=Stage(p);Parent(stage);File.Copy(Safe(Root,p.Path),stage,true);
        using(FileStream stream=new FileStream(stage,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            foreach(Region r in Regions[p.Path]) {
                if(r.Kind==1) {
                    if(install) {stream.Position=r.Offset;stream.Write(r.After,0,r.After.Length);}
                    else if(stream.Length>r.Offset && stream.Length<=r.Offset+r.After.Length) {
                        long length=p.BeforeBytes;
                        if(Active.OriginalLengths!=null && Active.OriginalLengths.ContainsKey(p.Path))length=Active.OriginalLengths[p.Path];
                        if(length<0 || length>p.BeforeBytes)throw new Exception("原始长度记录无效。");
                        stream.SetLength(length);
                    }
                    continue;
                }
                byte[] current=ReadAt(stream,r.Offset,r.Before.Length);
                for (int i = 0; i < current.Length; i++)
                    if (r.Before[i] != r.After[i])
                        current[i] = install ? r.After[i] : r.Before[i];
                stream.Position=r.Offset;stream.Write(current,0,current.Length);
            }
            stream.Flush(true);
        }
    }
    static void CleanTransactionFiles() {
        foreach(string target in M.Files.Select(p=>p.Path).Concat(M.Components.Select(c=>c.Path)))
            foreach(string path in new[] {Stage(target),Rollback(target)}) {RejectLinks(path);if(File.Exists(path))File.Delete(path);}
    }
    static void RecoverTransaction() {
        // Whole-file hashes here are transaction snapshots, never a release compatibility condition.
        foreach(TransactionFile t in Active.Transaction) {
            string current=HashIfExists(Safe(Root,t.Path));
            if(current==t.BeforeHash)continue;
            RejectLinks(Rollback(t.Path));
            if(current!=t.AfterHash || (t.Existed && HashIfExists(Rollback(t.Path))!=t.BeforeHash))throw new Exception("事务期间文件又被修改，已保留快照。请联系作者。文件 "+t.Path);
        }
        foreach(TransactionFile t in Active.Transaction) {
            string path=Safe(Root,t.Path);
            if(HashIfExists(path)==t.BeforeHash)continue;
            if(HashIfExists(path)!=t.AfterHash)throw new Exception("恢复前文件再次变化。 "+t.Path);
            if(t.Existed) {
                if(File.Exists(path))File.Replace(Rollback(t.Path),path,null);
                else {Parent(path);File.Move(Rollback(t.Path),path);}
            } else if(File.Exists(path))File.Delete(path);
        }
        foreach(TransactionFile t in Active.Transaction)if(HashIfExists(Safe(Root,t.Path))!=t.BeforeHash)throw new Exception("事务恢复校验失败。 "+t.Path);
        Active.Transaction=null;SaveState("Recovered");CleanTransactionFiles();Console.WriteLine("RECOVERY_SUCCESS：已恢复到本次操作前的文件。");
    }
    static bool OtherAsiMods() {
        foreach(string directory in new[] {Root,Safe(Root,"scripts"),Safe(Root,"plugins")}) {
            if(!Directory.Exists(directory))continue;
            SearchOption depth=directory==Root?SearchOption.TopDirectoryOnly:SearchOption.AllDirectories;
            if(Directory.EnumerateFiles(directory,"*.asi",depth).Any(path=>!path.Equals(Safe(Root,"P5SUnicodeInput.asi"),StringComparison.OrdinalIgnoreCase)))return true;
        }
        return false;
    }
    static List<ComponentChange> PlanComponents(bool install) {
        List<ComponentChange> changes=new List<ComponentChange>();
        bool otherMods=OtherAsiMods();
        foreach(Component component in M.Components) {
            string current=HashIfExists(Safe(Root,component.Path));
            bool owned=Active!=null && Active.ManagedPaths!=null && Active.ManagedPaths.ContainsKey(component.Path) && Active.ManagedPaths[component.Path];
            bool retain=!install && component.Shared && (!owned || otherMods || (current!=null && current!=component.SHA256));
            if(!install && component.SharedWith!=null) {
                ComponentChange owner=changes.Single(c=>c.Component.Path==component.SharedWith);
                retain=owner.AfterHash!=null;
            }
            if(current!=null && current!=component.SHA256 && !retain)
                throw new Exception("组件文件冲突 / Component conflict: "+component.Path+"。请保留该文件，确认其所属 Mod 后再安装或卸载。");
            string target=install?component.SHA256:retain?current:null;
            bool nextOwned=install && (!component.Shared || owned || current==null);
            changes.Add(new ComponentChange {Component=component,BeforeHash=current,AfterHash=target,Owned=nextOwned});
        }
        return changes;
    }
    static void SaveComponentOwnership(List<ComponentChange> changes) {
        if(Active.ManagedPaths==null)Active.ManagedPaths=new Dictionary<string,bool>();
        foreach(ComponentChange change in changes)Active.ManagedPaths[change.Component.Path]=change.Owned;
    }
    static void Change(bool install) {
        string[] states=M.Files.Select(p=>ResourceState(p)).ToArray();
        string wanted=install?"Patched":"Original";
        if(install && Active==null && states.Any(state=>state!="Original"))throw new Exception("本版本需要原版游戏文件。请先卸载旧版汉化或完整恢复 Steam 原版，再运行 Install.cmd。");
        List<ComponentChange> components=PlanComponents(install);
        if(states.All(x=>x==wanted) && components.All(c=>c.BeforeHash==c.AfterHash)) {
            string status=install?"Installed":"Uninstalled";
            if (Active == null || Active.Status != status || Active.Version != M.Version || Active.ManifestSHA256 != ExpectedManifest) {
                Confirm("资源区域已处于目标状态，将同步安装记录。");
                if (Active == null) Active = new State();
                SaveComponentOwnership(components);
                SaveState(status);
            }
            Console.WriteLine(install?"ALREADY_INSTALLED：汉化区域已完整。":"ALREADY_UNINSTALLED：汉化区域已恢复。");return;
        }
        EnsureGameClosed();
        PatchFile[] resources=M.Files.Where((p,i)=>states[i]!=wanted).ToArray();
        long required=resources.Sum(p=>Math.Max(new FileInfo(Safe(Root,p.Path)).Length,p.AfterBytes))+268435456;
        if(new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace<required)throw new Exception("游戏磁盘空间不足，需要约 "+Math.Ceiling(required/1073741824.0)+" GiB。");
        foreach(string target in resources.Select(p=>p.Path).Concat(components.Where(c=>c.BeforeHash!=c.AfterHash).Select(c=>c.Component.Path))) {
            string path=Safe(Root,target);
            if(!File.Exists(path))continue;
            if((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0)throw new Exception("目标文件为只读。 "+target);
            using(FileStream stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {}
        }
        Confirm(install?"将安装或补齐汉化涉及的区域，并保留范围外内容。":"将恢复汉化涉及的区域，并保留范围外内容。");
        if(Active==null)Active=new State();
        if(Active.OriginalLengths==null)Active.OriginalLengths=new Dictionary<string,long>();
        foreach(PatchFile p in M.Files)if(!Active.OriginalLengths.ContainsKey(p.Path))Active.OriginalLengths[p.Path]=Math.Min(new FileInfo(Safe(Root,p.Path)).Length,p.BeforeBytes);
        Active.Transaction=resources.Select(p=>new TransactionFile {Path=p.Path,Existed=true,BeforeHash=Hash(Safe(Root,p.Path))})
            .Concat(components.Where(c=>c.BeforeHash!=c.AfterHash).Select(c=>new TransactionFile {Path=c.Component.Path,Existed=c.BeforeHash!=null,BeforeHash=c.BeforeHash,AfterHash=c.AfterHash})).ToArray();
        SaveState("Preparing");
        try {
            CleanTransactionFiles();
            ValidateGuards();
            foreach(PatchFile p in M.Files)ResourceState(p);
            foreach(PatchFile resource in resources) {
                MakeStage(resource,install);
                Active.Transaction.Single(t=>t.Path==resource.Path).AfterHash=Hash(Stage(resource));
            }
            foreach(ComponentChange component in components.Where(c=>c.BeforeHash!=c.AfterHash && c.AfterHash!=null)) {
                string stage=Stage(component.Component.Path);
                Parent(stage);
                File.Copy(Safe(Package,component.Component.Payload),stage,true);
                if(Hash(stage)!=component.AfterHash)throw new Exception("组件暂存校验失败。 "+component.Component.Path);
            }
            SaveState("Committing");EnsureGameClosed();ValidateGuards();
            int count=0;
            foreach(TransactionFile transaction in Active.Transaction) {
                string path=Safe(Root,transaction.Path);
                if(HashIfExists(path)!=transaction.BeforeHash)throw new Exception("操作期间资源被其他程序改动。 "+transaction.Path);
                Parent(Rollback(transaction.Path));
                if(transaction.AfterHash==null)File.Move(path,Rollback(transaction.Path));
                else if(transaction.Existed)File.Replace(Stage(transaction.Path),path,Rollback(transaction.Path));
                else {Parent(path);File.Move(Stage(transaction.Path),path);}
                count++;Fault(count);
            }
            ValidateGuards();
            foreach(PatchFile p in M.Files)if(ResourceState(p)!=wanted)throw new Exception("汉化区域写入校验失败。 "+p.Path);
            foreach(ComponentChange component in components)if(HashIfExists(Safe(Root,component.Component.Path))!=component.AfterHash)throw new Exception("输入组件写入校验失败。 "+component.Component.Path);
            SaveComponentOwnership(components);
            Active.Transaction=null;SaveState(install?"Installed":"Uninstalled");CleanTransactionFiles();
            Console.WriteLine(install?"INSTALL_SUCCESS：汉化区域安装完成。":"UNINSTALL_SUCCESS：汉化区域恢复完成。");
        } catch {
            if(Active.Transaction!=null) {
                try {RecoverTransaction();}catch(Exception ex) {SaveState("RecoveryRequired");throw new Exception("事务恢复未完成，快照已保留。 "+ex.Message);}
            }
            throw;
        }
    }
        static string Safe(string parent,string relative) {

        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':')>=0) throw new Exception("Unsafe relative path.");

        string root=Path.GetFullPath(parent).TrimEnd('\\','/')+Path.DirectorySeparatorChar;

        string result=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));

        if (!result.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new Exception("Path escapes its root.");

        return result;

    }

    static void RejectLinks(string path) {

        string current=path;

        while (!String.IsNullOrEmpty(current)) {

            if ((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)

                throw new Exception("为保证备份位置可靠，此安装器不写入符号链接或目录联接 / Linked path: "+current);

            current=Path.GetDirectoryName(current);

        }

    }

    static void EnsureGameClosed() {

        foreach (Process p in Process.GetProcessesByName("game")) {

            using (p) {

                try {

                    if (String.Equals(p.MainModule.FileName,Safe(Root,"game.exe"),StringComparison.OrdinalIgnoreCase))

                        throw new InvalidOperationException("请先退出 P5S / Close Persona 5 Strikers before changing its files.");

                } catch (System.ComponentModel.Win32Exception) { throw new Exception("检测到无法确认路径的 game.exe，请先关闭该游戏进程。"); }

            }

        }

    }

    static string FindGame() {

        List<string> candidates=new List<string>();

        candidates.Add(Package);candidates.Add(Path.GetDirectoryName(Package.TrimEnd('\\','/')));
        string parent=Path.GetDirectoryName(Package.TrimEnd('\\','/'));
        if(!String.IsNullOrEmpty(parent)) candidates.Add(Path.GetDirectoryName(parent));

        List<string> steams=new List<string>();

        using (RegistryKey key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) if (key!=null && key.GetValue("SteamPath") is string) steams.Add((string)key.GetValue("SteamPath"));

        using (RegistryKey key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")) if (key!=null && key.GetValue("InstallPath") is string) steams.Add((string)key.GetValue("InstallPath"));

        foreach (string steam in steams.Distinct(StringComparer.OrdinalIgnoreCase)) {

            List<string> libraries=new List<string>();libraries.Add(steam);

            string vdf=Path.Combine(steam,@"steamapps\libraryfolders.vdf");

            if (File.Exists(vdf)) foreach (Match match in Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s*\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace(@"\\",@"\"));

            foreach (string lib in libraries) candidates.Add(Path.Combine(lib,@"steamapps\common\P5S"));

        }

        string[] found=candidates.Where(x=>!String.IsNullOrEmpty(x)&&File.Exists(Path.Combine(x,"game.exe"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (found.Length==1) return found[0];

        using (FolderBrowserDialog d=new FolderBrowserDialog()) {

            d.Description="选择 Persona 5 Strikers 的 P5S 文件夹（里面应有 game.exe）";d.ShowNewFolderButton=false;

            if (d.ShowDialog()!=DialogResult.OK) throw new OperationCanceledException();

            return d.SelectedPath;

        }

    }

    static void Confirm(string text) {

        Console.WriteLine(text);

        if (Yes) return;

        Console.Write("输入 Y 继续 / Enter Y to continue: ");

        if (!String.Equals(Console.ReadLine(),"Y",StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException();

    }

    static string StatePath { get { return Safe(Store,"state.json"); } }

    static string AcfValue(string text,string key) {
        Match match=Regex.Match(text,"\\\""+Regex.Escape(key)+"\\\"\\s*\\\"([^\\\"]*)\\\"",RegexOptions.IgnoreCase);
        return match.Success?match.Groups[1].Value:null;
    }
    static string AcfBlock(string text,string key) {
        Match match=Regex.Match(text,"\\\""+Regex.Escape(key)+"\\\"\\s*\\{([^{}]*)\\}",RegexOptions.IgnoreCase);
        return match.Success?match.Groups[1].Value:null;
    }
    static string LanguageLabel(string language) {
        switch((language??"").ToLowerInvariant()) {
            case "tchinese":return "繁体中文 (tchinese)";
            case "schinese":return "简体中文 (schinese)";
            case "english":return "英语 (english)";
            case "japanese":return "日语 (japanese)";
            default:return String.IsNullOrEmpty(language)?"未知":language;
        }
    }
    static void CheckSteamPrerequisites(string action) {
        DirectoryInfo common=Directory.GetParent(Root);
        DirectoryInfo steamapps=common==null?null:common.Parent;
        string path=steamapps==null?null:Path.Combine(steamapps.FullName,"appmanifest_1382330.acf");
        if(common==null || !common.Name.Equals("common",StringComparison.OrdinalIgnoreCase) || steamapps==null ||
           !steamapps.Name.Equals("steamapps",StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) {
            Console.WriteLine("前置检查：未找到对应 Steam 安装配置，无法自动确认语言；将继续严格校验游戏资源。请确认 Steam 游戏语言为繁体中文。");return;
        }
        string text;
        try {text=File.ReadAllText(path,Encoding.UTF8);} catch(IOException) {
            Console.WriteLine("前置检查：Steam 安装配置暂时无法读取，将以资源校验判断兼容性。");return;
        }
        if(AcfValue(text,"appid")!="1382330" || !String.Equals(AcfValue(text,"installdir"),Path.GetFileName(Root),StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine("前置检查：Steam 配置未对应当前目录，将以资源校验判断兼容性。");return;
        }
        string user=AcfBlock(text,"UserConfig"),mounted=AcfBlock(text,"MountedConfig");
        string language=user==null?null:AcfValue(user,"language");
        string active=mounted==null?null:AcfValue(mounted,"language");
        Console.WriteLine("Steam 游戏语言："+LanguageLabel(language));
        if(action=="install" && !String.IsNullOrEmpty(language) && !language.Equals("tchinese",StringComparison.OrdinalIgnoreCase))
            throw new Exception("LANGUAGE_REQUIRED：当前游戏语言是“"+LanguageLabel(language)+"”，本补丁需要“繁体中文”。\n请在 Steam 库右键 P5S → 属性 → 常规 → 语言，选择繁体中文。等待语言文件下载完成后，再运行 Install.cmd。\n本次尚未写入游戏文件。");
        if(action=="check" && !String.IsNullOrEmpty(language) && !language.Equals("tchinese",StringComparison.OrdinalIgnoreCase)) {
            PrerequisitesReady=false;Console.WriteLine("LANGUAGE_REQUIRED：安装前请在 Steam 游戏属性中将语言改为繁体中文，并等待下载完成。");
        }
        if(String.IsNullOrEmpty(language))Console.WriteLine("未读取到 Steam 的语言设置，仍会检查全部资源；请确认已选择繁体中文。");
        string flags=AcfValue(text,"StateFlags");
        bool busy=!String.IsNullOrEmpty(flags) && flags!="4";
        foreach(string[] pair in new[] {new[] {"BytesToDownload","BytesDownloaded"},new[] {"BytesToStage","BytesStaged"}}) {
            long total,done;if(Int64.TryParse(AcfValue(text,pair[0]),out total) && Int64.TryParse(AcfValue(text,pair[1]),out done) && total>done)busy=true;
        }
        if(!String.IsNullOrEmpty(language) && !String.IsNullOrEmpty(active) && !language.Equals(active,StringComparison.OrdinalIgnoreCase))busy=true;
        if(busy && action!="check")throw new Exception("STEAM_NOT_READY：Steam 的下载、更新或语言切换尚未完成。请等待 Steam 完成任务，再运行安装或卸载。\n当前 Steam 状态："+(flags??"未知")+"；已装载语言："+LanguageLabel(active)+"。本次尚未写入游戏文件。");
        if(busy) {PrerequisitesReady=false;Console.WriteLine("STEAM_NOT_READY：Steam 安装状态尚未就绪，此次仅作只读检查。");}
        string build=AcfValue(text,"buildid");Console.WriteLine("Steam Build："+(build??"未知"));
        if(!String.IsNullOrEmpty(build) && build!=M.SteamBuildId)Console.WriteLine("版本提示：本补丁的已验证 Build 为 "+M.SteamBuildId+"；当前 Build 不同，将根据资源校验决定是否可安装。");
    }
    static void ConfigureFaultTest() {

        string v=Environment.GetEnvironmentVariable("P5S_PATCH_TEST_FAIL_AFTER");

        if(String.IsNullOrEmpty(v))return;

        string marker=Safe(Root,".p5s-mod-test-sandbox");

        if(!File.Exists(marker) || File.ReadAllText(marker).Trim()!=ExpectedManifest)throw new Exception("Fault testing is allowed only in a marked test sandbox.");

        if(!Int32.TryParse(v,out FailAfter)||FailAfter<1||FailAfter>M.Files.Length+M.Components.Length)throw new Exception("Invalid fault test step.");

        CrashAfter=Environment.GetEnvironmentVariable("P5S_PATCH_TEST_CRASH")=="1";

    }

    static void Fault(int count) {

        if(count!=FailAfter)return;

        if(CrashAfter)Environment.Exit(91);

        throw new IOException("INTENTIONAL_TEST_FAILURE");

    }


}
