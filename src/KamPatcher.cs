// KaM TSK Win10/11 Fix 1.33 - by Daniel Airich
// Patches the user's own, already installed copy of Knights and Merchants: The Shattered Kingdom (patch 1.32).
// Contains NO game code. Changes are applied to the user's own EXE files by signature search.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("KaM TSK Win10/11 Fix")]
[assembly: AssemblyVersion("1.33.0.0")]
[assembly: AssemblyFileVersion("1.33.0.0")]
[assembly: AssemblyCompany("Daniel Airich")]
[assembly: AssemblyProduct("Knights and Merchants TSK Win10/11 Fix")]
[assembly: AssemblyCopyright("by Daniel Airich")]

namespace KamFix
{
    static class Info
    {
        public const string Version = "1.33";
        public const string Author = "Daniel Airich";
        public const string DdrawVersion = "v0.7.2";
        public const string DdrawZipUrl = "https://github.com/narzoul/DDrawCompat/releases/download/v0.7.2/DDrawCompat-v0.7.2.zip";
        public const string DdrawZipSha256 = "A932A4DCB2100552B6918FD4A3CC7DE138BB97448BBCA2A7051954993A397B39";
        public const string DdrawDllSha256 = "94895CF6EDE8FD0F74EE1447FDC08B7044A9C65A1A25A49A4DF893A0370975A3";
        public static readonly string[] Exes = new string[] { "KAM_1024.EXE", "KAM_800.EXE" };
        public const string BackupSuffix = ".orig_1.32";
        public static bool German { get { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de"; } }
        public static string T(string de, string en) { return German ? de : en; }
    }

    class Section { public uint Va, VSize, Raw, RSize; }

    class Pe
    {
        public byte[] D;
        public uint ImageBase;
        public List<Section> S = new List<Section>();
        public Pe(byte[] d)
        {
            D = d;
            if (d.Length < 0x200 || d[0] != 'M' || d[1] != 'Z') throw new Exception("not an EXE");
            int pe = BitConverter.ToInt32(d, 0x3C);
            if (pe < 0 || pe + 0x100 > d.Length || d[pe] != 'P' || d[pe + 1] != 'E') throw new Exception("not a PE file");
            int n = BitConverter.ToUInt16(d, pe + 6);
            int opt = BitConverter.ToUInt16(d, pe + 20);
            ImageBase = BitConverter.ToUInt32(d, pe + 24 + 28);
            int sh = pe + 24 + opt;
            for (int i = 0; i < n; i++)
            {
                int o = sh + i * 40;
                Section s = new Section();
                s.VSize = BitConverter.ToUInt32(d, o + 8);
                s.Va = BitConverter.ToUInt32(d, o + 12);
                s.RSize = BitConverter.ToUInt32(d, o + 16);
                s.Raw = BitConverter.ToUInt32(d, o + 20);
                S.Add(s);
            }
        }
        /// <summary>File offset of the name string of an import descriptor (case-insensitive), or -1.</summary>
        public int FindImportName(string dll)
        {
            int pe = BitConverter.ToInt32(D, 0x3C);
            uint impRva = BitConverter.ToUInt32(D, pe + 24 + 96 + 8);
            if (impRva == 0) return -1;
            int o = VaToOff(ImageBase + impRva);
            if (o < 0) return -1;
            for (; o + 20 <= D.Length; o += 20)
            {
                uint nameRva = BitConverter.ToUInt32(D, o + 12);
                if (nameRva == 0) break;
                int no = VaToOff(ImageBase + nameRva);
                if (no < 0) continue;
                int e = no;
                while (e < D.Length && D[e] != 0) e++;
                string s = Encoding.ASCII.GetString(D, no, e - no);
                if (string.Equals(s, dll, StringComparison.OrdinalIgnoreCase)) return no;
            }
            return -1;
        }

        public int VaToOff(uint va)
        {
            uint rva = va - ImageBase;
            foreach (Section s in S)
                if (rva >= s.Va && rva < s.Va + Math.Max(s.VSize, s.RSize) && rva - s.Va < s.RSize) return (int)(s.Raw + (rva - s.Va));
            return -1;
        }
        public uint OffToVa(int off)
        {
            foreach (Section s in S)
                if (off >= s.Raw && off < s.Raw + s.RSize) return ImageBase + s.Va + (uint)(off - s.Raw);
            throw new Exception("offset outside sections");
        }
    }

    class PatchResult
    {
        public bool Ok;
        public bool AlreadyPatched;
        public string Message = "";
    }

    static class ExePatcher
    {
        // original function that searches the CD drive: push ebp / mov ebp,esp / sub esp,22Ch / mov [ebp-22Ch],ecx / mov byte [ebp-1B4h],0 / jmp
        static readonly short[] SigA = Hex("55 8B EC 81 EC 2C 02 00 00 89 8D D4 FD FF FF C6 85 4C FE FF FF 00 EB 0E");
        static readonly short[] SigAPatched = Hex("55 8B EC 85 C9 74 0E 68 ?? ?? ?? ?? 51 E8 ?? ?? ?? ?? 83 C4 08 B8 01 00 00 00 5D C3 43 44 5C 00");
        // integrity check: cmp ecx,[ebp-14h] / jz skip / call showCdDialog / mov eax,1
        static readonly short[] SigB = Hex("3B 4D EC 74 0C E8 ?? ?? ?? ?? B8 01 00 00 00 EB 0B 8B 55 EC 89 15");
        static readonly short[] SigBPatched = Hex("3B 4D EC EB 0C E8 ?? ?? ?? ?? B8 01 00 00 00 EB 0B 8B 55 EC 89 15");
        // audio-CD track count check: cmp eax,[ebp-10h] / jbe skip / mov ecx,[ebp-48h] / mov edx,[ecx] / xor edx,1000h
        static readonly short[] SigD = Hex("3B 45 F0 76 10 8B 4D B8 8B 11 81 F2 00 10 00 00");
        static readonly short[] SigDPatched = Hex("3B 45 F0 90 90 8B 4D B8 8B 11 81 F2 00 10 00 00");
        // self checksum function
        static readonly short[] SigC = Hex("55 8B EC 83 EC 18 C7 45 E8 FF FF FF FF C7 45 F8 ?? ?? ?? ?? C7 45 F4 ?? ?? ?? ??");

        const int CallOffsetInA = 0x1CE;
        const int FuncLenA = 0x24D;

        static short[] Hex(string s)
        {
            string[] p = s.Split(' ');
            short[] r = new short[p.Length];
            for (int i = 0; i < p.Length; i++) r[i] = p[i] == "??" ? (short)-1 : Convert.ToInt16(p[i], 16);
            return r;
        }

        static List<int> Find(byte[] d, short[] sig)
        {
            List<int> r = new List<int>();
            for (int i = 0; i + sig.Length <= d.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < sig.Length; k++)
                    if (sig[k] >= 0 && d[i + k] != sig[k]) { ok = false; break; }
                if (ok) r.Add(i);
            }
            return r;
        }

        static List<int> FindU32(byte[] d, uint v)
        {
            List<int> r = new List<int>();
            byte[] p = BitConverter.GetBytes(v);
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == p[0] && d[i + 1] == p[1] && d[i + 2] == p[2] && d[i + 3] == p[3]) r.Add(i);
            return r;
        }

        class Chk { public uint A, B, S2, L2; }

        static Chk ReadChk(Pe pe, int off)
        {
            byte[] d = pe.D;
            Chk c = new Chk();
            c.A = BitConverter.ToUInt32(d, off + 16);
            c.B = BitConverter.ToUInt32(d, off + 23);
            // second range: next "C7 45 F8 imm32 C7 45 EC imm32"
            for (int i = off + 27; i < off + 0x200 && i + 14 < d.Length; i++)
            {
                if (d[i] == 0xC7 && d[i + 1] == 0x45 && d[i + 2] == 0xF8 && d[i + 7] == 0xC7 && d[i + 8] == 0x45 && d[i + 9] == 0xEC)
                {
                    c.S2 = BitConverter.ToUInt32(d, i + 3);
                    c.L2 = BitConverter.ToUInt32(d, i + 10);
                    return c;
                }
            }
            return null;
        }

        static uint Sum(Pe pe, Chk c)
        {
            uint s = 0xFFFFFFFF;
            int oa = pe.VaToOff(c.A);
            int os2 = pe.VaToOff(c.S2);
            if (oa < 0 || os2 < 0 || c.B <= c.A) throw new Exception("checksum range invalid");
            uint n = c.B - c.A;
            for (uint i = 0; i < n; i++) s -= (uint)(pe.D[oa + (int)i] & (i & 0xFF));
            uint lim = c.L2 - 4;
            for (uint j = 0; j < lim; j++) s ^= BitConverter.ToUInt32(pe.D, os2 + (int)j);
            return s;
        }

        public const string ProxyName = "KAMCD.dll";   // same length as "WINMM.dll"

        /// <summary>Redirects the EXE's import of WINMM.dll to our CD-audio proxy (KAMCD.dll). Import names are not part of the checksum.</summary>
        public static PatchResult ApplyMusic(byte[] d, bool dryRun)
        {
            PatchResult r = new PatchResult();
            Pe pe;
            try { pe = new Pe(d); } catch (Exception ex) { r.Message = ex.Message; return r; }
            int already = pe.FindImportName(ProxyName);
            if (already >= 0) { r.Ok = true; r.AlreadyPatched = true; r.Message = Info.T("Musik-Umleitung schon aktiv", "music redirect already active"); return r; }
            int o = pe.FindImportName("WINMM.dll");
            if (o < 0) { r.Message = Info.T("Import von WINMM.dll nicht gefunden.", "WINMM.dll import not found."); return r; }
            if (!dryRun)
            {
                byte[] n = Encoding.ASCII.GetBytes(ProxyName);
                Array.Copy(n, 0, d, o, n.Length);
                d[o + n.Length] = 0;
            }
            r.Ok = true; r.Message = Info.T("Musik-Umleitung gesetzt", "music redirect set");
            return r;
        }

        /// <summary>Patches the EXE image in place. Returns result with message.</summary>
        public static PatchResult Apply(byte[] d, bool dryRun)
        {
            PatchResult r = new PatchResult();
            Pe pe;
            try { pe = new Pe(d); } catch (Exception ex) { r.Message = ex.Message; return r; }

            List<int> a = Find(d, SigA), ap = Find(d, SigAPatched), b = Find(d, SigB), bp = Find(d, SigBPatched), c = Find(d, SigC);
            if (c.Count != 1) { r.Message = Info.T("Prüfsummenfunktion nicht gefunden - ist das KaM TSK mit Patch 1.32?", "Checksum routine not found - is this KaM TSK with patch 1.32?"); return r; }
            Chk chk = ReadChk(pe, c[0]);
            if (chk == null) { r.Message = "checksum parameters not found"; return r; }

            List<int> dd = Find(d, SigD), dp = Find(d, SigDPatched);
            bool aDone = ap.Count == 1 && a.Count == 0;
            bool bDone = bp.Count == 1 && b.Count == 0;
            bool dDone = dp.Count == 1 && dd.Count == 0;
            if (!aDone && a.Count != 1) { r.Message = Info.T("CD-Prüfung (A) nicht eindeutig gefunden.", "CD check (A) not found uniquely."); return r; }
            if (!bDone && b.Count != 1) { r.Message = Info.T("Integritätsprüfung (B) nicht eindeutig gefunden.", "Integrity check (B) not found uniquely."); return r; }
            if (!dDone && dd.Count != 1) { r.Message = Info.T("Audio-CD-Prüfung (D) nicht eindeutig gefunden.", "Audio CD check (D) not found uniquely."); return r; }

            uint oldSum = Sum(pe, chk);
            List<int> loc = FindU32(d, oldSum);
            if (loc.Count != 1)
            {
                r.Message = Info.T("Die Prüfsumme der EXE passt nicht zu einem unveränderten Patch-1.32-Spiel (fremde Änderungen?).",
                                   "The EXE checksum does not match an unmodified patch 1.32 game (foreign modifications?).");
                return r;
            }

            if (aDone && bDone && dDone) { r.Ok = true; r.AlreadyPatched = true; r.Message = Info.T("bereits gepatcht", "already patched"); return r; }
            if (dryRun) { r.Ok = true; r.Message = Info.T("bereit zum Patchen", "ready to patch"); return r; }

            if (!aDone)
            {
                int sA = a[0];
                if (sA + FuncLenA > d.Length || d[sA + FuncLenA - 1] != 0xC3 || d[sA + CallOffsetInA] != 0xE8 ||
                    d[sA + CallOffsetInA + 5] != 0x83 || d[sA + CallOffsetInA + 6] != 0xC4 || d[sA + CallOffsetInA + 7] != 0x08)
                { r.Message = Info.T("Struktur der CD-Prüfung unerwartet.", "Unexpected structure of the CD check."); return r; }
                uint callVa = pe.OffToVa(sA + CallOffsetInA);
                uint target = callVa + 5 + (uint)BitConverter.ToInt32(d, sA + CallOffsetInA + 1);
                uint fva = pe.OffToVa(sA);
                byte[] code = new byte[32];
                int p = 0;
                code[p++] = 0x55; code[p++] = 0x8B; code[p++] = 0xEC;          // push ebp; mov ebp,esp
                code[p++] = 0x85; code[p++] = 0xC9;                              // test ecx,ecx
                code[p++] = 0x74; code[p++] = 0x0E;                              // jz +14
                code[p++] = 0x68;                                                // push offset "CD\"
                Array.Copy(BitConverter.GetBytes(fva + 28), 0, code, p, 4); p += 4;
                code[p++] = 0x51;                                                // push ecx (destination)
                code[p++] = 0xE8;                                                // call strcpy-like
                Array.Copy(BitConverter.GetBytes(target - (fva + 18)), 0, code, p, 4); p += 4;
                code[p++] = 0x83; code[p++] = 0xC4; code[p++] = 0x08;            // add esp,8
                code[p++] = 0xB8; code[p++] = 1; code[p++] = 0; code[p++] = 0; code[p++] = 0; // mov eax,1
                code[p++] = 0x5D; code[p++] = 0xC3;                              // pop ebp; ret
                code[p++] = (byte)'C'; code[p++] = (byte)'D'; code[p++] = (byte)'\\'; code[p++] = 0;
                if (p != 32) { r.Message = "internal error (code size)"; return r; }
                Array.Copy(code, 0, d, sA, 32);
            }
            if (!bDone) d[b[0] + 3] = 0xEB;                                      // jz -> jmp (never show the "insert CD" dialog)
            if (!dDone) { d[dd[0] + 3] = 0x90; d[dd[0] + 4] = 0x90; }            // jbe -> nop nop (audio-CD track flag is always set)

            uint newSum = Sum(pe, chk);
            Array.Copy(BitConverter.GetBytes(newSum), 0, d, loc[0], 4);
            if (Sum(pe, chk) != BitConverter.ToUInt32(d, loc[0])) { r.Message = "checksum verification failed"; return r; }
            r.Ok = true;
            r.Message = Info.T("gepatcht", "patched");
            return r;
        }
    }

    /// <summary>Reads the audio tracks of the original CD through the Windows CD-ROM driver (raw CDDA reads) and writes WAV files.</summary>
    static class CdRipper
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        const uint IOCTL_CDROM_READ_TOC = 0x00024000;
        const uint IOCTL_CDROM_RAW_READ = 0x0002403E;
        const int SectorSize = 2352;

        public class Track { public int Number; public bool IsData; public int Start; public int End; }

        static IntPtr Open(char drive)
        {
            IntPtr h = CreateFile("\\\\.\\" + char.ToUpperInvariant(drive) + ":", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            return h;
        }

        public static List<Track> ReadToc(char drive)
        {
            List<Track> r = new List<Track>();
            IntPtr h = Open(drive);
            if (h == (IntPtr)(-1)) return r;
            try
            {
                byte[] toc = new byte[804];
                int got;
                if (!DeviceIoControl(h, IOCTL_CDROM_READ_TOC, null, 0, toc, toc.Length, out got, IntPtr.Zero)) return r;
                int len = (toc[0] << 8) | toc[1];
                int n = (len - 2) / 8;
                for (int i = 0; i < n && 4 + i * 8 + 8 <= toc.Length; i++)
                {
                    int o = 4 + i * 8;
                    Track t = new Track();
                    t.Number = toc[o + 2];
                    t.IsData = (toc[o + 1] & 0x04) != 0;
                    t.Start = (toc[o + 5] * 60 + toc[o + 6]) * 75 + toc[o + 7] - 150;
                    r.Add(t);
                }
                for (int i = 0; i + 1 < r.Count; i++) r[i].End = r[i + 1].Start;
                if (r.Count > 0) r.RemoveAt(r.Count - 1);   // lead-out entry (0xAA)
            }
            finally { CloseHandle(h); }
            return r;
        }

        public static int CountAudio(char drive)
        {
            int c = 0;
            foreach (Track t in ReadToc(drive)) if (!t.IsData) c++;
            return c;
        }

        static bool ReadSectors(IntPtr h, int lba, int count, byte[] buf)
        {
            byte[] inb = new byte[16];
            Array.Copy(BitConverter.GetBytes((long)lba * 2048), 0, inb, 0, 8);
            Array.Copy(BitConverter.GetBytes(count), 0, inb, 8, 4);
            Array.Copy(BitConverter.GetBytes(2), 0, inb, 12, 4);   // CDDA
            int got;
            return DeviceIoControl(h, IOCTL_CDROM_RAW_READ, inb, inb.Length, buf, count * SectorSize, out got, IntPtr.Zero) && got >= count * SectorSize;
        }

        /// <summary>Writes Track02.wav, Track03.wav ... into outDir. Returns number of tracks written.</summary>
        public static int Rip(char drive, string outDir, Action<string> log)
        {
            List<Track> toc = ReadToc(drive);
            IntPtr h = Open(drive);
            if (h == (IntPtr)(-1)) { log("CD: Laufwerk nicht lesbar"); return 0; }
            int written = 0;
            try
            {
                Directory.CreateDirectory(outDir);
                const int chunk = 16;
                byte[] buf = new byte[chunk * SectorSize];
                foreach (Track t in toc)
                {
                    if (t.IsData) continue;
                    string path = Path.Combine(outDir, "Track" + t.Number.ToString("00") + ".wav");
                    int total = t.End - t.Start;
                    if (File.Exists(path) && new FileInfo(path).Length == 44L + (long)total * SectorSize) { written++; continue; }
                    string tmp = path + ".part";
                    int bad = 0;
                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    using (BinaryWriter w = new BinaryWriter(fs))
                    {
                        long dataLen = (long)total * SectorSize;
                        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((int)(36 + dataLen)); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                        w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
                        w.Write(Encoding.ASCII.GetBytes("data")); w.Write((int)dataLen);
                        int pos = 0;
                        while (pos < total)
                        {
                            int n = Math.Min(chunk, total - pos);
                            bool ok = false;
                            for (int attempt = 0; attempt < 4 && !ok; attempt++) ok = ReadSectors(h, t.Start + pos, n, buf);
                            if (!ok)
                            {
                                // sector-wise fallback, zero-fill what cannot be read
                                for (int s = 0; s < n; s++)
                                {
                                    byte[] one = new byte[SectorSize];
                                    bool o1 = false;
                                    for (int attempt = 0; attempt < 3 && !o1; attempt++) o1 = ReadSectors(h, t.Start + pos + s, 1, one);
                                    if (!o1) { Array.Clear(one, 0, one.Length); bad++; }
                                    Array.Copy(one, 0, buf, s * SectorSize, SectorSize);
                                }
                            }
                            w.Write(buf, 0, n * SectorSize);
                            pos += n;
                        }
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    written++;
                    log("  Track " + t.Number.ToString("00") + ": " + (total / 75 / 60) + ":" + ((total / 75) % 60).ToString("00") + (bad > 0 ? "  (" + bad + " nicht lesbare Sektoren)" : ""));
                }
            }
            finally { CloseHandle(h); }
            return written;
        }
    }

    class Installer
    {
        public Action<string> Log;
        public bool CopyCd = true, UseDdraw = true, Shortcuts = true, Music = true;
        public string CdSource;           // optional folder/drive with the CD content
        public Func<string> AskCdFolder;  // called if no CD found (may return null)

        void L(string s) { if (Log != null) Log(s); }

        public static string FindExe(string dir, string name)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (string f in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        public static bool LooksLikeGame(string dir)
        {
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                   (FindExe(dir, "KAM_1024.EXE") != null || FindExe(dir, "KAM_800.EXE") != null) && Directory.Exists(Path.Combine(dir, "Data"));
        }

        public static string AutoDetect()
        {
            List<string> c = new List<string>();
            c.Add(Environment.CurrentDirectory);
            c.Add(AppDomain.CurrentDomain.BaseDirectory);
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(pf86)) c.Add(Path.Combine(pf86, "KnightsAndMerchants"));
            c.Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "C:\\Program Files", "KnightsAndMerchants"));
            c.Add("C:\\Retro\\KnightsAndMerchants");
            c.Add("C:\\Games\\KnightsAndMerchants");
            foreach (string d in c) if (LooksLikeGame(d)) return d;
            return null;
        }

        static string FindCdRoot()
        {
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType == DriveType.CDRom && di.IsReady && IsCdRoot(di.RootDirectory.FullName)) return di.RootDirectory.FullName;
                }
                catch { }
            }
            return null;
        }

        static bool IsCdRoot(string root)
        {
            return File.Exists(Path.Combine(root, "AUTORUN.EXE")) && Directory.Exists(Path.Combine(root, "VIDEO"));
        }

        static long CopyDir(string src, string dst, Action<string> log)
        {
            long n = 0;
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                string t = Path.Combine(dst, Path.GetFileName(f));
                FileInfo fi = new FileInfo(f);
                if (File.Exists(t) && new FileInfo(t).Length == fi.Length) { n += fi.Length; continue; }
                File.Copy(f, t, true);
                File.SetAttributes(t, FileAttributes.Normal);
                n += fi.Length;
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(d);
                if (string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                n += CopyDir(d, Path.Combine(dst, name), log);
            }
            return n;
        }

        static string Sha256(byte[] b)
        {
            using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "");
        }

        public bool Install(string dir)
        {
            L(Info.T("Spielordner: ", "Game folder: ") + dir);
            if (!LooksLikeGame(dir)) { L(Info.T("FEHLER: Kein Knights-and-Merchants-Ordner (KAM_1024.EXE und Data fehlen).", "ERROR: Not a Knights and Merchants folder (KAM_1024.EXE and Data missing).")); return false; }

            // 1. dry run on all EXEs first
            List<string> exes = new List<string>();
            foreach (string n in Info.Exes)
            {
                string f = FindExe(dir, n);
                if (f == null) continue;
                PatchResult pr = ExePatcher.Apply(File.ReadAllBytes(f), true);
                L(Path.GetFileName(f) + ": " + pr.Message);
                if (!pr.Ok) { L(Info.T("Abbruch - es wurde nichts verändert.", "Aborted - nothing was changed.")); return false; }
                exes.Add(f);
            }
            if (exes.Count == 0) { L("ERROR: no EXE"); return false; }

            // 2. CD content
            string cdDir = Path.Combine(dir, "CD");
            if (CopyCd)
            {
                bool have = Directory.Exists(Path.Combine(cdDir, "VIDEO")) && File.Exists(Path.Combine(cdDir, "AUTORUN.EXE"));
                if (have) L(Info.T("CD-Inhalt ist schon im Spielordner (CD\\).", "CD content already in game folder (CD\\)."));
                else
                {
                    string src = CdSource;
                    if (string.IsNullOrEmpty(src) || !IsCdRoot(src)) src = FindCdRoot();
                    if (src == null && AskCdFolder != null) { string s = AskCdFolder(); if (!string.IsNullOrEmpty(s) && IsCdRoot(s)) src = s; }
                    if (src == null) { L(Info.T("FEHLER: Keine Original-CD / kein Image gefunden (AUTORUN.EXE + VIDEO). Bitte CD einlegen oder ISO einbinden und erneut starten.", "ERROR: No original CD / image found (AUTORUN.EXE + VIDEO). Insert the CD or mount the ISO and run again.")); return false; }
                    L(Info.T("Kopiere CD-Inhalt von ", "Copying CD content from ") + src + " ...");
                    long n = CopyDir(src, cdDir, L);
                    L(Info.T("CD-Inhalt kopiert: ", "CD content copied: ") + (n / 1048576) + " MB");
                }
            }

            // 2b. music: rip the audio tracks of the original CD and install the CD-audio proxy
            bool musicReady = false;
            if (Music) musicReady = PrepareMusic(dir);

            // 3. patch EXEs (with backup)
            foreach (string f in exes)
            {
                string bak = f + Info.BackupSuffix;
                byte[] data = File.ReadAllBytes(f);
                PatchResult pr = ExePatcher.Apply(data, false);
                if (!pr.Ok) { L(Path.GetFileName(f) + ": " + pr.Message); return false; }
                PatchResult pm = musicReady ? ExePatcher.ApplyMusic(data, false) : null;
                if (pm != null && !pm.Ok) { L(Path.GetFileName(f) + ": " + pm.Message); return false; }
                bool changed = !pr.AlreadyPatched || (pm != null && !pm.AlreadyPatched);
                if (!changed) { L(Path.GetFileName(f) + ": " + pr.Message); continue; }
                if (!File.Exists(bak)) File.Copy(f, bak);
                File.WriteAllBytes(f, data);
                L(Path.GetFileName(f) + ": " + pr.Message + (pm != null ? " + " + pm.Message : "") + Info.T(" (Sicherung: ", " (backup: ") + Path.GetFileName(bak) + ")");
            }

            // 4. DDrawCompat
            if (UseDdraw && !InstallDdraw(dir)) return false;

            // 5. marker + shortcuts
            try
            {
                File.WriteAllText(Path.Combine(dir, "KaM_TSK_Fix.txt"),
                    "Knights and Merchants - The Shattered Kingdom\r\nWin10/11 Fix " + Info.Version + " by " + Info.Author + "\r\nApplied: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n" +
                    "CD check removed, self-checksum updated, DDrawCompat " + Info.DdrawVersion + " (BSD Zero Clause License, narzoul).\r\n");
            }
            catch { }
            if (Shortcuts) MakeShortcuts(dir, exes);
            L(Info.T("FERTIG. Starte das Spiel über die Desktop-Verknüpfung (Arbeitsverzeichnis = Spielordner).", "DONE. Start the game with the desktop shortcut (working directory = game folder)."));
            return true;
        }

        static byte[] LoadProxyDll()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KAMCD.dll"))
            {
                if (s != null) { using (MemoryStream ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            }
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KAMCD.dll");
            return File.Exists(local) ? File.ReadAllBytes(local) : null;
        }

        static char FindAudioCd()
        {
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType != DriveType.CDRom || !di.IsReady) continue;
                    if (!IsCdRoot(di.RootDirectory.FullName)) continue;
                    if (CdRipper.CountAudio(di.Name[0]) > 0) return di.Name[0];
                }
                catch { }
            }
            return '\0';
        }

        bool PrepareMusic(string dir)
        {
            string md = Path.Combine(dir, "Music");
            int have = Directory.Exists(md) ? Directory.GetFiles(md, "Track*.wav").Length + Directory.GetFiles(md, "Track*.mp3").Length : 0;
            if (have == 0)
            {
                char cd = FindAudioCd();
                if (cd == '\0')
                {
                    L(Info.T("Musik: Keine Original-CD mit Audio-Spuren gefunden (ein ISO-Image hat keine). Musik wird übersprungen - Original-CD einlegen und den Patcher erneut ausführen.",
                             "Music: no original CD with audio tracks found (an ISO image has none). Skipping music - insert the original CD and run the patcher again."));
                    return false;
                }
                L(Info.T("Lese Audio-Tracks von Laufwerk ", "Reading audio tracks from drive ") + cd + ": ... (" + Info.T("dauert einige Minuten", "takes a few minutes") + ")");
                int n = CdRipper.Rip(cd, md, L);
                if (n == 0) { L(Info.T("Musik: Auslesen fehlgeschlagen.", "Music: reading failed.")); return false; }
                L(Info.T("Musik: ", "Music: ") + n + Info.T(" Tracks als WAV gespeichert.", " tracks saved as WAV."));
            }
            else L(Info.T("Musik-Dateien sind schon vorhanden (Music\\).", "Music files already present (Music\\)."));
            byte[] dll = LoadProxyDll();
            if (dll == null) { L("ERROR: KAMCD.dll not available"); return false; }
            File.WriteAllBytes(Path.Combine(dir, "KAMCD.dll"), dll);
            L(Info.T("KAMCD.dll (CD-Audio-Ersatz) installiert.", "KAMCD.dll (CD audio replacement) installed."));
            return true;
        }

        bool InstallDdraw(string dir)
        {
            string target = Path.Combine(dir, "ddraw.dll");
            if (File.Exists(target) && Sha256(File.ReadAllBytes(target)) == Info.DdrawDllSha256) { L("ddraw.dll (DDrawCompat " + Info.DdrawVersion + "): " + Info.T("bereits vorhanden", "already present")); return true; }
            byte[] dll = null;
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ddraw.dll");
            if (File.Exists(local) && Sha256(File.ReadAllBytes(local)) == Info.DdrawDllSha256) dll = File.ReadAllBytes(local);
            if (dll == null)
            {
                try
                {
                    L(Info.T("Lade DDrawCompat ", "Downloading DDrawCompat ") + Info.DdrawVersion + " (GitHub) ...");
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    byte[] zip;
                    using (WebClient wc = new WebClient()) { wc.Headers["User-Agent"] = "KaM-TSK-Fix/" + Info.Version; zip = wc.DownloadData(Info.DdrawZipUrl); }
                    if (Sha256(zip) != Info.DdrawZipSha256) { L(Info.T("FEHLER: Prüfsumme des Downloads stimmt nicht - nicht installiert.", "ERROR: download checksum mismatch - not installed.")); return false; }
                    using (ZipArchive za = new ZipArchive(new MemoryStream(zip)))
                    {
                        ZipArchiveEntry e = za.GetEntry("ddraw.dll");
                        if (e == null) { L("ERROR: ddraw.dll not in archive"); return false; }
                        using (Stream s = e.Open()) using (MemoryStream ms = new MemoryStream()) { s.CopyTo(ms); dll = ms.ToArray(); }
                    }
                    if (Sha256(dll) != Info.DdrawDllSha256) { L(Info.T("FEHLER: ddraw.dll-Prüfsumme falsch.", "ERROR: ddraw.dll checksum wrong.")); return false; }
                }
                catch (Exception ex)
                {
                    L(Info.T("FEHLER beim Download: ", "Download ERROR: ") + ex.Message);
                    L(Info.T("Tipp: ddraw.dll (DDrawCompat " + Info.DdrawVersion + ") neben diese EXE legen und erneut starten.", "Tip: put ddraw.dll (DDrawCompat " + Info.DdrawVersion + ") next to this EXE and run again."));
                    return false;
                }
            }
            if (File.Exists(target) && !File.Exists(target + ".bak_kamfix")) File.Copy(target, target + ".bak_kamfix");
            File.WriteAllBytes(target, dll);
            L("ddraw.dll (DDrawCompat " + Info.DdrawVersion + "): " + Info.T("installiert", "installed"));
            return true;
        }

        static void MakeShortcuts(string dir, List<string> exes)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                foreach (string f in exes)
                {
                    string res = f.ToUpperInvariant().Contains("1024") ? "1024x768" : "800x600";
                    object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { Path.Combine(desk, "Knights and Merchants TSK (" + res + ").lnk") });
                    Type lt = lnk.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { f });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { dir });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                }
            }
            catch { }
        }

        public bool Uninstall(string dir)
        {
            bool any = false;
            foreach (string n in Info.Exes)
            {
                string f = FindExe(dir, n);
                if (f == null) continue;
                string bak = f + Info.BackupSuffix;
                if (File.Exists(bak))
                {
                    try { File.Copy(bak, f, true); File.Delete(bak); L(Path.GetFileName(f) + ": " + Info.T("Original wiederhergestellt", "original restored")); any = true; }
                    catch (IOException) { L(Path.GetFileName(f) + ": " + Info.T("FEHLER - Datei ist in Benutzung. Spiel beenden und erneut versuchen.", "ERROR - file is in use. Close the game and try again.")); return false; }
                }
            }
            string dd = Path.Combine(dir, "ddraw.dll");
            if (File.Exists(dd) && Sha256(File.ReadAllBytes(dd)) == Info.DdrawDllSha256)
            {
                File.Delete(dd);
                if (File.Exists(dd + ".bak_kamfix")) File.Move(dd + ".bak_kamfix", dd);
                L(Info.T("ddraw.dll entfernt", "ddraw.dll removed")); any = true;
            }
            string kc = Path.Combine(dir, "KAMCD.dll");
            if (File.Exists(kc)) { try { File.Delete(kc); L(Info.T("KAMCD.dll entfernt", "KAMCD.dll removed")); any = true; } catch (IOException) { L(Info.T("KAMCD.dll in Benutzung - Spiel beenden.", "KAMCD.dll in use - close the game.")); } }
            string m = Path.Combine(dir, "KaM_TSK_Fix.txt");
            if (File.Exists(m)) File.Delete(m);
            L(any ? Info.T("Rückgängig gemacht. (Der Ordner CD\\ bleibt erhalten.)", "Undone. (The CD\\ folder was kept.)") : Info.T("Nichts zum Rückgängigmachen gefunden.", "Nothing to undo found."));
            return true;
        }
    }

    /// <summary>Medieval look: parchment, wood/leather banner, gold lettering - own artwork, no game assets.</summary>
    class KButton : Button
    {
        bool hover, down;
        public KButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            Font = new Font("Georgia", 11f, FontStyle.Bold); ForeColor = Color.FromArgb(244, 218, 128);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color top = Color.FromArgb(112, 72, 38), bot = Color.FromArgb(62, 38, 18);
            if (!Enabled) { top = Color.FromArgb(120, 108, 92); bot = Color.FromArgb(90, 80, 68); }
            else if (down) { top = Color.FromArgb(54, 33, 15); bot = Color.FromArgb(86, 55, 28); }
            else if (hover) { top = Color.FromArgb(142, 96, 52); bot = Color.FromArgb(84, 52, 26); }
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(r, top, bot, 90f)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Color.FromArgb(Enabled ? 214 : 150, 170, 60), 2f)) g.DrawRectangle(p, 2, 2, Width - 5, Height - 5);
            using (Pen p = new Pen(Color.FromArgb(34, 20, 8), 1f)) g.DrawRectangle(p, r);
            StringFormat sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            RectangleF tr = new RectangleF(0, down ? 1 : 0, Width, Height);
            using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) g.DrawString(Text, Font, sh, new RectangleF(tr.X + 1, tr.Y + 1, tr.Width, tr.Height), sf);
            using (Brush fb = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(190, 180, 160))) g.DrawString(Text, Font, fb, tr, sf);
        }
    }

    class MainForm : Form
    {
        static readonly Color Ink = Color.FromArgb(52, 34, 14);
        TextBox path = new TextBox();
        TextBox log = new TextBox();
        CheckBox cbCd = new CheckBox(), cbDd = new CheckBox(), cbSc = new CheckBox(), cbMu = new CheckBox();
        KButton bInstall = new KButton(), bUndo = new KButton(), bBrowse = new KButton();

        static CheckBox Box(string text, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.Checked = true; c.BackColor = Color.Transparent; c.ForeColor = Ink;
            c.Font = new Font("Georgia", 9.5f); c.SetBounds(30, y, 690, 22); c.Cursor = Cursors.Hand;
            return c;
        }

        public MainForm(string dir)
        {
            Text = "Knights and Merchants TSK - Win10/11 Fix " + Info.Version + " - by " + Info.Author;
            ClientSize = new Size(760, 640); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            DoubleBuffered = true;
            Font = new Font("Georgia", 9.5f);
            try
            {
                string exe = dir != null ? (Installer.FindExe(dir, "KAM_1024.EXE") ?? Installer.FindExe(dir, "KAM_800.EXE")) : null;
                if (exe != null) Icon = Icon.ExtractAssociatedIcon(exe);   // icon of the user's own game
            }
            catch { }

            Label l1 = new Label(); l1.Text = Info.T("Spielordner (offizieller Patch 1.32 muss installiert sein):", "Game folder (official patch 1.32 must be installed):");
            l1.BackColor = Color.Transparent; l1.ForeColor = Ink; l1.Font = new Font("Georgia", 10f, FontStyle.Bold); l1.SetBounds(30, 140, 700, 22);
            path.SetBounds(30, 166, 590, 26); path.Text = dir ?? ""; path.Font = new Font("Georgia", 10f);
            path.BackColor = Color.FromArgb(244, 234, 206); path.ForeColor = Ink; path.BorderStyle = BorderStyle.FixedSingle;
            bBrowse.Text = Info.T("Suchen", "Browse"); bBrowse.Font = new Font("Georgia", 9.5f, FontStyle.Bold); bBrowse.SetBounds(630, 164, 100, 30);
            bBrowse.Click += delegate { using (FolderBrowserDialog f = new FolderBrowserDialog()) { if (f.ShowDialog() == DialogResult.OK) path.Text = f.SelectedPath; } };

            cbCd = Box(Info.T("CD-Abfrage entfernen (CD-Inhalt ins Spiel kopieren, ca. 240 MB)", "Remove the CD check (copy the CD content into the game, ~240 MB)"), 206);
            cbDd = Box(Info.T("Grafik-Fix für Windows 10/11 (DDrawCompat " + Info.DdrawVersion + ", Download von GitHub)", "Graphics fix for Windows 10/11 (DDrawCompat " + Info.DdrawVersion + ", downloaded from GitHub)"), 232);
            cbMu = Box(Info.T("Musik ohne CD: Audio-Tracks der Original-CD als WAV ablegen (ca. 400-600 MB, Original-CD im Laufwerk)", "Music without CD: save the original CD's audio tracks as WAV (~400-600 MB, original CD in the drive)"), 258);
            cbSc = Box(Info.T("Desktop-Verknüpfungen anlegen", "Create desktop shortcuts"), 284);

            bInstall.Text = Info.T("Installieren", "Install"); bInstall.SetBounds(30, 322, 200, 44);
            bUndo.Text = Info.T("Rückgängig", "Undo"); bUndo.SetBounds(244, 322, 200, 44);
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.SetBounds(30, 384, 700, 206);
            log.Font = new Font("Consolas", 9f); log.BackColor = Color.FromArgb(236, 224, 192); log.ForeColor = Ink; log.BorderStyle = BorderStyle.FixedSingle;
            Label foot = new Label(); foot.BackColor = Color.Transparent; foot.ForeColor = Color.FromArgb(96, 70, 38); foot.Font = new Font("Georgia", 8.5f, FontStyle.Italic);
            foot.Text = Info.T("Enthält keinen Spielcode. Ändert nur deine eigene Installation, Sicherungen werden angelegt. Inoffizielles Fan-Werkzeug.", "Contains no game code. Only modifies your own installation, backups are created. Unofficial fan tool.");
            foot.SetBounds(30, 600, 700, 20);
            Label by = new Label(); by.BackColor = Color.Transparent; by.ForeColor = Color.FromArgb(120, 24, 20); by.Font = new Font("Georgia", 11f, FontStyle.Bold | FontStyle.Italic);
            by.Text = "By " + Info.Author + "  -  open source (MIT)"; by.TextAlign = ContentAlignment.MiddleRight; by.SetBounds(450, 332, 280, 26);
            Controls.AddRange(new Control[] { l1, path, bBrowse, cbCd, cbDd, cbMu, cbSc, bInstall, bUndo, log, foot, by });
            bInstall.Click += delegate { Run(true); };
            bUndo.Click += delegate { Run(false); };
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle all = ClientRectangle;
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(all, Color.FromArgb(226, 210, 164), Color.FromArgb(196, 172, 118), 90f)) g.FillRectangle(b, all);
            // faint parchment fibres
            Random rnd = new Random(7);
            using (Pen p = new Pen(Color.FromArgb(14, 90, 60, 20)))
                for (int i = 0; i < 160; i++) { int x = rnd.Next(all.Width), y = rnd.Next(all.Height); g.DrawLine(p, x, y, x + rnd.Next(10, 60), y + rnd.Next(-3, 4)); }
            // banner (leather/wood)
            Rectangle ban = new Rectangle(0, 0, all.Width, 118);
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(ban, Color.FromArgb(92, 58, 28), Color.FromArgb(44, 26, 12), 90f)) g.FillRectangle(b, ban);
            using (Pen p = new Pen(Color.FromArgb(214, 170, 60), 3f)) g.DrawLine(p, 0, 116, all.Width, 116);
            using (Pen p = new Pen(Color.FromArgb(30, 16, 6), 2f)) g.DrawLine(p, 0, 119, all.Width, 119);
            // shield crest
            System.Drawing.Drawing2D.GraphicsPath sh = new System.Drawing.Drawing2D.GraphicsPath();
            sh.AddLine(26, 16, 98, 16); sh.AddLine(98, 16, 98, 62); sh.AddBezier(98, 62, 98, 90, 70, 104, 62, 108); sh.AddBezier(62, 108, 54, 104, 26, 90, 26, 62); sh.CloseFigure();
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(26, 16, 72, 92), Color.FromArgb(178, 38, 30), Color.FromArgb(104, 18, 16), 90f)) g.FillPath(b, sh);
            using (Pen p = new Pen(Color.FromArgb(232, 190, 80), 3f)) g.DrawPath(p, sh);
            using (Pen p = new Pen(Color.FromArgb(240, 214, 130), 4f))
            {   // sword
                g.DrawLine(p, 62, 28, 62, 86); g.DrawLine(p, 46, 44, 78, 44);
                g.DrawLine(p, 62, 86, 62, 90);
            }
            using (Brush gb = new SolidBrush(Color.FromArgb(240, 214, 130))) g.FillEllipse(gb, 57, 90, 10, 10);
            // title
            using (Font f1 = new Font("Georgia", 24f, FontStyle.Bold))
            using (Font f2 = new Font("Georgia", 11.5f, FontStyle.Italic))
            using (Brush shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            using (Brush gold = new SolidBrush(Color.FromArgb(244, 214, 116)))
            using (Brush cream = new SolidBrush(Color.FromArgb(236, 222, 186)))
            {
                g.DrawString("KNIGHTS AND MERCHANTS", f1, shadow, 123, 22);
                g.DrawString("KNIGHTS AND MERCHANTS", f1, gold, 120, 19);
                g.DrawString("The Shattered Kingdom  -  Win10/11 Fix " + Info.Version, f2, cream, 122, 66);
                g.DrawString("by " + Info.Author, f2, gold, 122, 88);
            }
            // double border around the parchment area
            using (Pen p = new Pen(Color.FromArgb(120, 84, 40), 2f)) g.DrawRectangle(p, 12, 128, all.Width - 25, all.Height - 141);
            using (Pen p = new Pen(Color.FromArgb(190, 150, 70), 1f)) g.DrawRectangle(p, 16, 132, all.Width - 33, all.Height - 149);
        }

        void Append(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), s); return; }
            log.AppendText(s + "\r\n");
        }

        void Run(bool install)
        {
            string dir = path.Text.Trim();
            bool cd = cbCd.Checked, dd = cbDd.Checked, sc = cbSc.Checked, mu = cbMu.Checked;
            bInstall.Enabled = bUndo.Enabled = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    Installer inst = new Installer();
                    inst.Log = Append; inst.CopyCd = cd; inst.UseDdraw = dd; inst.Shortcuts = sc; inst.Music = mu;
                    inst.AskCdFolder = delegate()
                    {
                        string r = null;
                        Invoke(new Action(delegate()
                        {
                            if (MessageBox.Show(Info.T("Keine Original-CD gefunden. Ordner mit dem CD-Inhalt (z.B. eingebundenes Image) auswählen?", "No original CD found. Pick the folder with the CD content (e.g. a mounted image)?"), Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
                                using (FolderBrowserDialog f = new FolderBrowserDialog()) { if (f.ShowDialog() == DialogResult.OK) r = f.SelectedPath; }
                        }));
                        return r;
                    };
                    if (install) inst.Install(dir); else inst.Uninstall(dir);
                }
                catch (Exception ex) { Append("ERROR: " + ex.Message); }
                BeginInvoke(new Action(delegate() { bInstall.Enabled = bUndo.Enabled = true; }));
            });
            t.IsBackground = true; t.Start();
        }
    }

    static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            try { AttachConsole(-1); } catch { }
            // CLI: --selftest <exe> | /silent [/dir=PATH] [/nocd] [/noddraw] [/noshortcut] [/undo]
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                byte[] d = File.ReadAllBytes(args[1]);
                PatchResult r1 = ExePatcher.Apply(d, false);
                Console.WriteLine(Path.GetFileName(args[1]) + ": " + r1.Message + " ok=" + r1.Ok);
                PatchResult rm = ExePatcher.ApplyMusic(d, false);
                Console.WriteLine("music import: " + rm.Message + " ok=" + rm.Ok);
                if (!rm.Ok) return 1;
                if (r1.Ok && args.Length >= 3) File.WriteAllBytes(args[2], d);
                PatchResult r2 = ExePatcher.Apply(d, false);
                Console.WriteLine("second run: " + r2.Message + " ok=" + r2.Ok + " already=" + r2.AlreadyPatched);
                return r1.Ok && r2.AlreadyPatched ? 0 : 1;
            }
            bool silent = false, undo = false; string dir = null; Installer cli = new Installer();
            foreach (string a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "/silent" || l == "-silent") silent = true;
                else if (l == "/undo") undo = true;
                else if (l == "/nocd") cli.CopyCd = false;
                else if (l == "/noddraw") cli.UseDdraw = false;
                else if (l == "/nomusic") cli.Music = false;
                else if (l == "/noshortcut") cli.Shortcuts = false;
                else if (l.StartsWith("/dir=")) dir = a.Substring(5).Trim('"');
                else if (l.StartsWith("/cd=")) cli.CdSource = a.Substring(4).Trim('"');
            }
            if (dir == null) dir = Installer.AutoDetect();
            if (silent)
            {
                cli.Log = delegate(string s) { Console.WriteLine(s); };
                if (dir == null) { Console.WriteLine("game folder not found (use /dir=)"); return 2; }
                bool ok = undo ? cli.Uninstall(dir) : cli.Install(dir);
                return ok ? 0 : 1;
            }
            Application.EnableVisualStyles();
            Application.Run(new MainForm(dir));
            return 0;
        }
    }
}

