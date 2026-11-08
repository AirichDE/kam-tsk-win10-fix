// Prints the peak level (0..1) of the default audio output device, sampled repeatedly. Used to verify that sound is really played.
using System;
using System.Runtime.InteropServices;
using System.Threading;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCls { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}
[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    int GetPeakValue(out float peak);
}

class PeakMeter
{
    static int Main(string[] a)
    {
        int seconds = a.Length > 0 ? int.Parse(a[0]) : 10;
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCls();
        IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
        Guid iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        IAudioMeterInformation m = (IAudioMeterInformation)o;
        float max = 0; int nonzero = 0, n = 0;
        DateTime end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end) { float p; m.GetPeakValue(out p); if (p > max) max = p; if (p > 0.001f) nonzero++; n++; Thread.Sleep(100); }
        Console.WriteLine("samples=" + n + " nonzero=" + nonzero + " maxPeak=" + max.ToString("0.000"));
        return 0;
    }
}
