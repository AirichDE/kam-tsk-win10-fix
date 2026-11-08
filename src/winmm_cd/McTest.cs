// Test harness for the winmm_cd proxy: replays the MCI call sequence the game uses.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Sink : NativeWindow
{
    public System.Collections.Generic.List<string> Msgs = new System.Collections.Generic.List<string>();
    public Sink() { CreateParams cp = new CreateParams(); cp.Caption = "sink"; CreateHandle(cp); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x3B9) lock (Msgs) Msgs.Add("MM_MCINOTIFY wParam=" + m.WParam + " lParam=0x" + m.LParam.ToInt32().ToString("x"));
        base.WndProc(ref m);
    }
}

class McTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr LoadLibrary(string f);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string n);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Cmd(uint id, uint msg, IntPtr flags, IntPtr param);

    static Cmd cmd;

    static uint Send(uint id, uint msg, uint flags, IntPtr p) { return cmd(id, msg, new IntPtr(flags), p); }

    static void Pump(Sink s, int ms) { DateTime end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); } }

    static int Main(string[] a)
    {
        string dll = a[0];
        IntPtr h = LoadLibrary(dll);
        if (h == IntPtr.Zero) { Console.WriteLine("LoadLibrary failed"); return 1; }
        cmd = (Cmd)Marshal.GetDelegateForFunctionPointer(GetProcAddress(h, "mciSendCommandA"), typeof(Cmd));
        Sink sink = new Sink();

        // OPEN cdaudio (MCI_OPEN_TYPE | MCI_WAIT)
        IntPtr op = Marshal.AllocHGlobal(32); IntPtr name = Marshal.StringToHGlobalAnsi("cdaudio");
        Marshal.WriteInt32(op, 0, 0); Marshal.WriteInt32(op, 4, 0); Marshal.WriteInt32(op, 8, name.ToInt32());
        uint e = Send(0, 0x803, 0x2002, op);
        uint id = (uint)Marshal.ReadInt32(op, 4);
        Console.WriteLine("OPEN err=" + e + " id=0x" + id.ToString("x"));

        // SET time format TMSF
        IntPtr sp = Marshal.AllocHGlobal(16); Marshal.WriteInt32(sp, 0, 0); Marshal.WriteInt32(sp, 4, 10);
        Console.WriteLine("SET err=" + Send(id, 0x80D, 0x400, sp));

        // STATUS number of tracks
        IntPtr st = Marshal.AllocHGlobal(16);
        Marshal.WriteInt32(st, 0, 0); Marshal.WriteInt32(st, 4, 0); Marshal.WriteInt32(st, 8, 3); Marshal.WriteInt32(st, 12, 0);
        e = Send(id, 0x814, 0x100, st);
        int tracks = Marshal.ReadInt32(st, 4);
        Console.WriteLine("STATUS tracks err=" + e + " n=" + tracks);

        // STATUS length per track (MCI_STATUS_ITEM | MCI_TRACK), TMSF result
        int totalSec = 0;
        for (int t = 1; t <= tracks; t++)
        {
            Marshal.WriteInt32(st, 4, 0); Marshal.WriteInt32(st, 8, 1); Marshal.WriteInt32(st, 12, t);
            e = Send(id, 0x814, 0x110, st);
            int r = Marshal.ReadInt32(st, 4);
            int min = r & 0xFF, sec = (r >> 8) & 0xFF;
            totalSec += min * 60 + sec;
            Console.WriteLine("  track " + t + " err=" + e + " length " + min + ":" + sec.ToString("00"));
        }
        Console.WriteLine("total length sec = " + totalSec);

        // PLAY track 2 -> 3 with notify
        IntPtr pp = Marshal.AllocHGlobal(12);
        Action<int> play = delegate(int t)
        {
            Marshal.WriteInt32(pp, 0, sink.Handle.ToInt32()); Marshal.WriteInt32(pp, 4, t); Marshal.WriteInt32(pp, 8, t + 1);
            Console.WriteLine("PLAY " + t + " err=" + Send(id, 0x806, 0xD, pp));
        };
        play(2);
        Pump(sink, 800);
        Marshal.WriteInt32(st, 4, 0); Marshal.WriteInt32(st, 8, 4); Marshal.WriteInt32(st, 12, 0);
        Send(id, 0x814, 0x100, st);
        Console.WriteLine("mode while playing = " + Marshal.ReadInt32(st, 4) + " (526=play)");
        Pump(sink, 4500);
        lock (sink.Msgs) Console.WriteLine("after track 2 should have ended: " + string.Join(" | ", sink.Msgs.ToArray()));
        sink.Msgs.Clear();

        play(3); Pump(sink, 600);
        play(4); Pump(sink, 600);
        Console.WriteLine("STOP err=" + Send(id, 0x808, 2, IntPtr.Zero));
        Pump(sink, 300);
        lock (sink.Msgs) Console.WriteLine("after supersede+stop: " + string.Join(" | ", sink.Msgs.ToArray()));
        Marshal.WriteInt32(st, 4, 0); Marshal.WriteInt32(st, 8, 4);
        Send(id, 0x814, 0x100, st);
        Console.WriteLine("mode after stop = " + Marshal.ReadInt32(st, 4) + " (525=stop)");
        Console.WriteLine("CLOSE err=" + Send(id, 0x804, 2, IntPtr.Zero));

        // unknown device id must be forwarded to the real winmm (error expected, no crash)
        Console.WriteLine("forward test (bad id) err=" + Send(0x9999, 0x814, 0x100, st));
        return 0;
    }
}
