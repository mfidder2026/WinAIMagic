using System.Runtime.InteropServices;
using WinAIMagic.Engine;

namespace WinAIMagic.App;

/// <summary>The default microphone as 16 kHz mono samples, through Windows' wave input (winmm), in pieces of 100 ms on a thread of its own.
/// The sound stays in memory; nothing is written to disk here.</summary>
public sealed class Microphone : IDisposable
{
    private const int PieceSamples = Audio.Rate / 10;
    private const int Buffers = 8;
    private const int HeaderSize = 48;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const int WhdrDone = 1;

    private IntPtr handle;
    private readonly IntPtr[] headers = new IntPtr[Buffers];
    private readonly IntPtr[] data = new IntPtr[Buffers];
    private Thread? thread;
    private volatile bool running;

    /// <summary>Every piece of sound, on the microphone's thread.</summary>
    public Action<short[]>? OnPiece { get; set; }

    /// <summary>Opens the default microphone and starts; null, or the error code of Windows.</summary>
    public int? Start()
    {
        var format = new WaveFormat { Tag = 1, Channels = 1, Rate = Audio.Rate, BytesPerSecond = Audio.Rate * 2, BlockAlign = 2, Bits = 16 };
        int r = waveInOpen(out handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) return r;
        for (int i = 0; i < Buffers; i++)
        {
            data[i] = Marshal.AllocHGlobal(PieceSamples * 2);
            headers[i] = Marshal.AllocHGlobal(HeaderSize);
            for (int b = 0; b < HeaderSize; b += 8) Marshal.WriteInt64(headers[i], b, 0);
            Marshal.WriteIntPtr(headers[i], 0, data[i]);
            Marshal.WriteInt32(headers[i], 8, PieceSamples * 2);
            waveInPrepareHeader(handle, headers[i], HeaderSize);
            waveInAddBuffer(handle, headers[i], HeaderSize);
        }
        running = true;
        r = waveInStart(handle);
        if (r != 0) { Dispose(); return r; }
        thread = new Thread(Loop) { IsBackground = true, Name = "Microphone" };
        thread.Start();
        return null;
    }

    private void Loop()
    {
        int next = 0;
        while (running)
        {
            var h = headers[next];
            if ((Marshal.ReadInt32(h, 24) & WhdrDone) == 0) { Thread.Sleep(15); continue; }
            int bytes = Marshal.ReadInt32(h, 12);
            var piece = new short[bytes / 2];
            Marshal.Copy(data[next], piece, 0, piece.Length);
            if (!running) break;
            Marshal.WriteInt32(h, 24, Marshal.ReadInt32(h, 24) & ~WhdrDone);
            waveInAddBuffer(handle, h, HeaderSize);
            if (piece.Length > 0) OnPiece?.Invoke(piece);
            next = (next + 1) % Buffers;
        }
    }

    public void Dispose()
    {
        running = false;
        if (thread != null && thread != Thread.CurrentThread) thread.Join(500);
        thread = null;
        if (handle != IntPtr.Zero)
        {
            waveInStop(handle);
            waveInReset(handle);
            for (int i = 0; i < Buffers; i++)
                if (headers[i] != IntPtr.Zero) waveInUnprepareHeader(handle, headers[i], HeaderSize);
            waveInClose(handle);
            handle = IntPtr.Zero;
        }
        for (int i = 0; i < Buffers; i++)
        {
            if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
            if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
            headers[i] = data[i] = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short Tag, Channels;
        public int Rate, BytesPerSecond;
        public short BlockAlign, Bits, Extra;
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr handle);
}
