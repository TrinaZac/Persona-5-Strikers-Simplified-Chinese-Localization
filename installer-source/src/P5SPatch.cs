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
    public string Path, BeforeSHA256, AfterSHA256, Delta, DeltaSHA256;
    public long BeforeBytes, AfterBytes, DeltaBytes;
    public int Records;
}
public class ProtectedFile { public string Path, SHA256; public long Bytes; }
public class Manifest {
    public int Format, SteamAppId;
    public string ModId, Version, Date, SteamBuildId, Language;
    public long RequiredFreeBytes;
    public PatchFile[] Files;
    public ProtectedFile[] Protected;
}
public class State {
    public string ModId, Version, ManifestSHA256, Status, Time;
}
public static class P5SPatch {
    // Filled by build_release.py; a modified package manifest is rejected.
    const string ExpectedManifest = "1be8a849582dd9842f42144b9f58e63f07071d5f71bf088a326fc14cffa197b4";
    const string StateFolder = ".p5s-simplified-chinese";
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string Package, Root, Store;
    static Manifest M;
    static bool Yes;
    static int FailAfter;
    static bool CrashAfter;

    [STAThread]
    public static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("P5S 简体中文补丁 / Simplified Chinese Patch 1.0.0");
        Console.WriteLine("Windows Steam 版 | 游戏语言请选择繁体中文 / Traditional Chinese\n");
        try {
            Package = AppDomain.CurrentDomain.BaseDirectory;
            string action = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
            if (action == "credits" || action == "--credits") {
                using (Stream stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("P5S.Credits.txt"))
                using (StreamReader reader=new StreamReader(stream,Encoding.UTF8)) Console.WriteLine(reader.ReadToEnd());
                return 0;
            }
            if (action == "help" || action == "--help") {
                Console.WriteLine("完整解压后双击 Install.cmd 安装，Uninstall.cmd 卸载。请保留 Core 文件夹。");
                Console.WriteLine("安装中断后再次运行 Install.cmd 或 Uninstall.cmd，即可恢复原版。版权和许可：P5SPatch.exe --credits");
                Console.WriteLine("CLI: P5SPatch.exe install|uninstall|check|recover --game <folder> [--yes]");
                return 0;
            }
            if (!new[] {"install","uninstall","check","recover"}.Contains(action)) throw new Exception("Unknown action.");
            string selected = null;
            for (int i=1;i<args.Length;i++) {
                if (args[i]=="--game" && i+1<args.Length) selected=args[++i];
                else if (args[i]=="--yes") Yes=true;
                else throw new Exception("Unknown or incomplete argument: "+args[i]);
            }
            CheckHash(Safe(Package,"manifest.json"),ExpectedManifest);
            M=Json.Deserialize<Manifest>(File.ReadAllText(Safe(Package,"manifest.json"),Encoding.UTF8));
            if (M.Format!=1 || M.ModId!="p5s-simplified-chinese" || M.Version!="1.0.0" || M.Files.Length!=4) throw new Exception("Unsupported manifest.");
            Root=Path.GetFullPath(selected ?? FindGame()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if (!File.Exists(Safe(Root,"game.exe"))) throw new Exception("没有找到 game.exe，请选择 Persona 5 Strikers 的 P5S 游戏目录。");
            Store=Safe(Root,StateFolder);
            Console.WriteLine("游戏目录 / Game: "+Root);
            string lockName="Local\\P5S_SC_"+Hex(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))).Substring(0,24);
            using (Mutex gate=new Mutex(false,lockName)) {
                bool owned=false;
                try {
                    try { owned=gate.WaitOne(0); } catch (AbandonedMutexException) { owned=true; }
                    if (!owned) throw new Exception("另一个补丁操作正在运行 / Another patch operation is running.");
                    RejectLinks(Root);
                    foreach (PatchFile p in M.Files) RejectLinks(Safe(Root,p.Path));
                    foreach (ProtectedFile p in M.Protected) RejectLinks(Safe(Root,p.Path));
                    RejectLinks(Store);
                    if (action!="check") EnsureGameClosed();
                    ConfigureFaultTest();
                    foreach (ProtectedFile p in M.Protected) {
                        Console.WriteLine("检查版本 / Checking: "+p.Path);
                        CheckFile(Safe(Root,p.Path),p.SHA256,p.Bytes);
                    }
                    if (action=="check") return Check();
                    if (action=="install") Install();
                    else if (action=="uninstall") Uninstall();
                    else Recover();
                    return 0;
                } finally { if (owned) gate.ReleaseMutex(); }
            }
        } catch (OperationCanceledException) { Console.WriteLine("已取消，未修改游戏 / Cancelled."); return 2; }
        catch (Exception ex) {
            Console.WriteLine("\n操作未完成 / FAILED: "+ex.Message);
            if (ex is UnauthorizedAccessException) Console.WriteLine("目录写入权限不足。请关闭游戏，并在需要时以管理员身份运行对应 CMD 入口。");
            Console.WriteLine("请保留完整错误信息与游戏目录中的 "+StateFolder+" 文件夹。");
            return 1;
        }
    }
    static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-","").ToLowerInvariant(); }
    static string Hash(string path) {
        using (FileStream f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using (SHA256 h=SHA256.Create()) return Hex(h.ComputeHash(f));
    }
    static void CheckHash(string path,string hash) {
        if (!File.Exists(path) || !String.Equals(Hash(path),hash,StringComparison.OrdinalIgnoreCase)) throw new Exception("文件校验不匹配 / Hash mismatch: "+path);
    }
    static void CheckFile(string path,string hash,long length) {
        if (!File.Exists(path) || new FileInfo(path).Length!=length) throw new Exception("文件缺失或大小不符 / File missing or wrong size: "+path);
        CheckHash(path,hash);
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
    static State ReadState() {
        if (!File.Exists(StatePath)) return null;
        State s=Json.Deserialize<State>(File.ReadAllText(StatePath,Encoding.UTF8));
        if (s.ModId!=M.ModId || s.Version!=M.Version || s.ManifestSHA256!=ExpectedManifest) throw new Exception("备份属于其他补丁版本，请使用对应版本的卸载程序 / Backup belongs to a different version.");
        return s;
    }
    static void SaveState(string status) {
        Directory.CreateDirectory(Store);
        State s=new State {ModId=M.ModId,Version=M.Version,ManifestSHA256=ExpectedManifest,Status=status,Time=DateTimeOffset.Now.ToString("o")};
        byte[] bytes=new UTF8Encoding(false).GetBytes(Json.Serialize(s));string tmp=Safe(Store,"state.json.tmp");
        using (FileStream f=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)) { f.Write(bytes,0,bytes.Length);f.Flush(true); }
        if (File.Exists(StatePath)) File.Replace(tmp,StatePath,null);else File.Move(tmp,StatePath);
    }
    static string Backup(PatchFile p) { return Safe(Store,"backup/"+p.Path); }
    static string Stage(PatchFile p) { return Safe(Store,"stage/"+p.Path+".new"); }
    static void Parent(string p) { RejectLinks(p);Directory.CreateDirectory(Path.GetDirectoryName(p)); }
    static bool IsPending(State s) { return s!=null && new[] {"Preparing","Committing","Uninstalling","RecoveryRequired"}.Contains(s.Status); }
    static int Check() {
        int originals=0,installed=0;
        foreach (PatchFile p in M.Files) {
            string h=Hash(Safe(Root,p.Path));
            string state=h==p.BeforeSHA256?"原版 / Original":h==p.AfterSHA256?"已汉化 / Patched":"不匹配 / Different version or conflicting mod";
            if(h==p.BeforeSHA256)originals++;if(h==p.AfterSHA256)installed++;
            Console.WriteLine(state+" : "+p.Path);
        }
        State s=ReadState();
        if(IsPending(s)) {Console.WriteLine("存在未完成操作，请运行 Install.cmd 或 Uninstall.cmd 恢复 / Run Install.cmd or Uninstall.cmd to recover.");return 3;}
        if(originals==M.Files.Length) {Console.WriteLine("CHECK_ORIGINAL_OK：可安装 / Ready to install.");return 0;}
        if(installed==M.Files.Length) {
            Console.WriteLine(s!=null&&s.Status=="Installed"?"CHECK_INSTALLED_OK":"CHECK_PATCHED_NO_MANAGED_BACKUP：内容匹配，但没有本安装器的原版备份。");return 0;
        }
        Console.WriteLine("CHECK_INCOMPATIBLE：请先在 Steam 验证游戏文件，并移除修改同一资源包的其他 Mod。");return 3;
    }
    static void CheckWritable() {
        foreach (PatchFile p in M.Files) {
            string path=Safe(Root,p.Path);
            if((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0)throw new Exception("目标文件为只读 / Read-only file: "+p.Path);
            using(FileStream f=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {}
        }
    }
    static void CheckSpace(long required) {
        DriveInfo drive=new DriveInfo(Path.GetPathRoot(Root));
        if(drive.AvailableFreeSpace<required)throw new Exception("游戏磁盘可用空间不足，需要约 "+Math.Ceiling(required/1073741824.0)+" GB / Not enough free space.");
    }
    static void Install() {
        State s=ReadState();
        if(IsPending(s)) { Recover();Console.WriteLine("原版已恢复；如需安装补丁，请再次运行 Install.cmd / Run Install.cmd again to install.");return; }
        foreach(PatchFile p in M.Files)CheckFile(Safe(Package,p.Delta),p.DeltaSHA256,p.DeltaBytes);
        if(s!=null && s.Status=="Installed") {
            foreach(PatchFile p in M.Files) {CheckFile(Safe(Root,p.Path),p.AfterSHA256,p.AfterBytes);CheckFile(Backup(p),p.BeforeSHA256,p.BeforeBytes);}
            Console.WriteLine("ALREADY_INSTALLED：当前版本已经安装，原始备份保留。");return;
        }
        foreach(PatchFile p in M.Files) {Console.WriteLine("核验原版 / Original: "+p.Path);CheckFile(Safe(Root,p.Path),p.BeforeSHA256,p.BeforeBytes);}
        CheckSpace(M.RequiredFreeBytes);CheckWritable();
        Confirm("将安装完整简中补丁，并自动备份 4 个将被修改的文件。安装期间请勿启动游戏。");
        SaveState("Preparing");
        try {
            foreach(PatchFile p in M.Files) {
                Console.WriteLine("备份并生成 / Preparing: "+p.Path);
                string backup=Backup(p);Parent(backup);
                if(!File.Exists(backup))File.Copy(Safe(Root,p.Path),backup,false);
                CheckFile(backup,p.BeforeSHA256,p.BeforeBytes);
                ApplyDelta(p,backup,Stage(p));
            }
            EnsureGameClosed();CheckWritable();SaveState("Committing");int count=0;
            foreach(PatchFile p in M.Files) {
                CheckFile(Safe(Root,p.Path),p.BeforeSHA256,p.BeforeBytes);
                File.Replace(Stage(p),Safe(Root,p.Path),null);count++;Fault(count);
            }
            foreach(PatchFile p in M.Files)CheckFile(Safe(Root,p.Path),p.AfterSHA256,p.AfterBytes);
            foreach(ProtectedFile p in M.Protected)CheckFile(Safe(Root,p.Path),p.SHA256,p.Bytes);
            SaveState("Installed");
            Console.WriteLine("INSTALL_SUCCESS：安装完成。请将游戏语言设为繁体中文，然后正常从 Steam 启动游戏。");
        } catch {
            Console.WriteLine("安装未完成，正在恢复原版 / Restoring original files...");
            try {RestoreOriginals();SaveState("Uninstalled");Console.WriteLine("ROLLBACK_VERIFIED：所有游戏文件已核验恢复。");}
            catch(Exception rollback) {SaveState("RecoveryRequired");throw new Exception("恢复未完成，请保留备份并运行 Uninstall.cmd。 "+rollback.Message);}
            throw;
        }
    }
    static void Uninstall() {
        State s=ReadState();
        if(s==null)throw new Exception("找不到本安装器的原始备份。请使用原安装工具卸载，或通过 Steam 验证游戏文件。");
        if(IsPending(s)) { Recover();return; }
        if(s.Status=="Uninstalled") {
            foreach(PatchFile p in M.Files)CheckFile(Safe(Root,p.Path),p.BeforeSHA256,p.BeforeBytes);
            Console.WriteLine("ALREADY_UNINSTALLED：游戏已是原版。");return;
        }
        foreach(PatchFile p in M.Files) {CheckFile(Safe(Root,p.Path),p.AfterSHA256,p.AfterBytes);CheckFile(Backup(p),p.BeforeSHA256,p.BeforeBytes);}
        CheckSpace(M.Files.Sum(x=>x.BeforeBytes)+268435456);CheckWritable();
        Confirm("将恢复安装前的原版文件。存档不会改动。备份将继续保留。");
        SaveState("Uninstalling");
        try {RestoreOriginals();SaveState("Uninstalled");Console.WriteLine("UNINSTALL_SUCCESS：已恢复原版，全部文件核验通过。");}
        catch {SaveState("RecoveryRequired");throw;}
    }
    static void Recover() {
        State s=ReadState();if(s==null)throw new Exception("没有可恢复的安装记录 / No recovery record.");
        if(s.Status=="Installed")throw new Exception("安装完整，请用 Uninstall.cmd 卸载 / Use Uninstall.cmd.");
        CheckWritable();CheckSpace(M.Files.Sum(x=>x.BeforeBytes)+268435456);
        Confirm("将检查中断的补丁操作，并用已核验的备份恢复原版文件。");
        RestoreOriginals();SaveState("Uninstalled");Console.WriteLine("RECOVERY_SUCCESS：所有原版文件恢复并通过校验。");
    }
    static void RestoreOriginals() {
        List<PatchFile> restore=new List<PatchFile>();
        // Validate all affected files before restoring any of them.
        foreach(PatchFile p in M.Files) {
            string h=Hash(Safe(Root,p.Path));if(h==p.BeforeSHA256)continue;
            if(h!=p.AfterSHA256)throw new Exception("文件又被其他程序修改，拒绝覆盖 / Unexpected external change: "+p.Path);
            CheckFile(Backup(p),p.BeforeSHA256,p.BeforeBytes);restore.Add(p);
        }
        foreach(PatchFile p in restore) {string staged=Stage(p);Parent(staged);File.Copy(Backup(p),staged,true);CheckFile(staged,p.BeforeSHA256,p.BeforeBytes);}
        EnsureGameClosed();
        foreach(PatchFile p in restore) {
            CheckFile(Safe(Root,p.Path),p.AfterSHA256,p.AfterBytes);File.Replace(Stage(p),Safe(Root,p.Path),null);
        }
        foreach(PatchFile p in M.Files)CheckFile(Safe(Root,p.Path),p.BeforeSHA256,p.BeforeBytes);
    }
    static byte[] ReadExact(BinaryReader reader,int count) {
        byte[] b=reader.ReadBytes(count);if(b.Length!=count)throw new Exception("Truncated patch.");return b;
    }
    static void ApplyDelta(PatchFile p,string original,string staged) {
        Parent(staged);File.Copy(original,staged,true);
        using(FileStream input=File.OpenRead(Safe(Package,p.Delta)))
        using(GZipStream gzip=new GZipStream(input,CompressionMode.Decompress))
        using(BinaryReader r=new BinaryReader(gzip))
        using(FileStream target=new FileStream(staged,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            if(Encoding.ASCII.GetString(ReadExact(r,8))!="P5SDLT1\0")throw new Exception("Bad patch format.");
            long before=r.ReadInt64(),after=r.ReadInt64();string bh=Hex(ReadExact(r,32)),ah=Hex(ReadExact(r,32));uint count=r.ReadUInt32();
            if(before!=p.BeforeBytes || after!=p.AfterBytes || bh!=p.BeforeSHA256 || ah!=p.AfterSHA256 || count!=p.Records || count>100000)throw new Exception("Patch header mismatch.");
            target.SetLength(after);long previous=0;
            for(uint i=0;i<count;i++) {
                long off=r.ReadInt64();uint length=r.ReadUInt32();
                if(length==0 || length>65536 || off<previous || off>after-length)throw new Exception("Patch range is invalid.");
                byte[] diff=ReadExact(r,(int)length),buffer=new byte[length];target.Position=off;int got=0;
                while(got<buffer.Length) {int n=target.Read(buffer,got,buffer.Length-got);if(n==0)throw new Exception("Unexpected file end.");got+=n;}
                for(int j=0;j<buffer.Length;j++)buffer[j]^=diff[j];
                target.Position=off;target.Write(buffer,0,buffer.Length);previous=off+length;
            }
            if(gzip.ReadByte()!=-1)throw new Exception("Unexpected data after patch records.");target.Flush(true);
        }
        CheckFile(staged,p.AfterSHA256,p.AfterBytes);
    }
    static void ConfigureFaultTest() {
        string v=Environment.GetEnvironmentVariable("P5S_PATCH_TEST_FAIL_AFTER");
        if(String.IsNullOrEmpty(v))return;
        string marker=Safe(Root,".p5s-mod-test-sandbox");
        if(!File.Exists(marker) || File.ReadAllText(marker).Trim()!=ExpectedManifest)throw new Exception("Fault testing is allowed only in a marked test sandbox.");
        if(!Int32.TryParse(v,out FailAfter)||FailAfter<1||FailAfter>4)throw new Exception("Invalid fault test step.");
        CrashAfter=Environment.GetEnvironmentVariable("P5S_PATCH_TEST_CRASH")=="1";
    }
    static void Fault(int count) {
        if(count!=FailAfter)return;
        if(CrashAfter)Environment.Exit(91);
        throw new IOException("INTENTIONAL_TEST_FAILURE");
    }
}
