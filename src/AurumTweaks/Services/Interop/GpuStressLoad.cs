using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Serilog;

namespace AurumTweaks.Services.Interop;

/// <summary>Starts/stops a real GPU compute load for the integrated stability test.</summary>
public interface IGpuStressLoad
{
    /// <summary>Begin sustained GPU compute work on a background thread. Returns false (and an honest
    /// reason) when no D3D11 hardware GPU is available — NEVER silently falls back to the CPU/WARP
    /// rasterizer, which would report zero real GPU work.</summary>
    bool Start(out string error);

    /// <summary>Stop the load and release the GPU resources. Safe to call when not started.</summary>
    void Stop();

    /// <summary>True while a load is running.</summary>
    bool IsRunning { get; }
}

/// <summary>
/// Real GPU load via raw-P/Invoke Direct3D 11 compute (no third-party dependency): create a HARDWARE
/// device, runtime-compile an ALU-hammer compute shader, and dispatch it in a loop on a background thread.
/// Verified end-to-end on the dev machine (RTX 4080 SUPER): D3D11CreateDevice + D3DCompile + CreateComputeShader
/// + CreateBuffer + CreateUnorderedAccessView + repeated Dispatch all succeed. Each dispatch is deliberately
/// bounded (small grid × a 4096-iteration inner loop) and paced on the CPU side so the loop saturates the
/// GPU WITHOUT tripping Windows' 2 s TDR watchdog on its own — a real bad overclock is what should trip it.
/// The immediate context is used only from the loop thread (D3D contexts aren't thread-safe).
/// </summary>
public sealed class GpuStressLoad : IGpuStressLoad
{
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint FEATURE_LEVEL_11_0 = 0xB000;

    // Vtable slots verified on-device (see the d3dprobe): device CreateBuffer=3, CreateUAV=8,
    // CreateComputeShader=18; context Dispatch=41, CSSetUnorderedAccessViews=66, CSSetShader=67.
    private const int DEV_CREATE_BUFFER = 3, DEV_CREATE_UAV = 8, DEV_CREATE_COMPUTE_SHADER = 18;
    private const int CTX_DISPATCH = 41, CTX_CS_SET_UAV = 66, CTX_CS_SET_SHADER = 67;
    private const int BLOB_GET_PTR = 3, BLOB_GET_SIZE = 4;

    private const uint Elements = 1 << 16;   // 65 536 floats — bounded so a single dispatch never trips TDR

    private const string Hlsl = @"
RWStructuredBuffer<float> Output : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    float acc = (float)id.x * 1e-4f + 1.0f; float b = 0.9999f;
    [loop] for (int i = 0; i < 4096; i++) { acc = mad(acc, b, 0.5f); acc = mad(acc, 1.0001f, -0.4999f); b = mad(b, 0.99999f, 1e-5f); }
    Output[id.x] = acc;
}";

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        [In] uint[] featureLevels, uint numFeatureLevels, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr context);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, IntPtr srcLen, string name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPtrDel(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateComputeShaderDel(IntPtr self, IntPtr bc, IntPtr len, IntPtr link, out IntPtr cs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBufferDel(IntPtr self, ref BufferDesc d, IntPtr init, out IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateUavDel(IntPtr self, IntPtr res, IntPtr desc, out IntPtr uav);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CsSetShaderDel(IntPtr self, IntPtr sh, IntPtr ci, uint n);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CsSetUavDel(IntPtr self, uint start, uint num, ref IntPtr uavs, IntPtr counts);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DispatchDel(IntPtr self, uint x, uint y, uint z);

    private static T Fn<T>(IntPtr obj, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    private readonly object _sync = new();
    private IntPtr _device, _ctx, _shader, _buffer, _uav;
    private Thread? _loop;
    private volatile bool _run;

    public bool IsRunning => _run;

    public bool Start(out string error)
    {
        error = string.Empty;
        lock (_sync)
        {
            if (_run) return true;
            try
            {
                int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                    new[] { FEATURE_LEVEL_11_0 }, 1, D3D11_SDK_VERSION, out _device, out _, out _ctx);
                if (hr < 0 || _device == IntPtr.Zero)
                {
                    error = "Aucun GPU compatible Direct3D 11 accessible (test GPU indisponible — ex. session distante / VM sans GPU).";
                    Cleanup();
                    return false;
                }

                byte[] src = Encoding.ASCII.GetBytes(Hlsl);
                if (D3DCompile(src, (IntPtr)src.Length, "stress.hlsl", IntPtr.Zero, IntPtr.Zero, "CSMain", "cs_5_0", 0, 0, out IntPtr code, out IntPtr errBlob) < 0 || code == IntPtr.Zero)
                {
                    if (errBlob != IntPtr.Zero) Marshal.Release(errBlob);
                    error = "Compilation du shader de charge GPU échouée.";
                    Cleanup();
                    return false;
                }
                if (errBlob != IntPtr.Zero) Marshal.Release(errBlob);

                IntPtr bc = Fn<BlobPtrDel>(code, BLOB_GET_PTR)(code);
                IntPtr len = Fn<BlobPtrDel>(code, BLOB_GET_SIZE)(code);
                Fn<CreateComputeShaderDel>(_device, DEV_CREATE_COMPUTE_SHADER)(_device, bc, len, IntPtr.Zero, out _shader);
                Marshal.Release(code);

                var bd = new BufferDesc { ByteWidth = Elements * 4, Usage = 0, BindFlags = 0x80, CpuAccessFlags = 0, MiscFlags = 0x40, StructureByteStride = 4 };
                Fn<CreateBufferDel>(_device, DEV_CREATE_BUFFER)(_device, ref bd, IntPtr.Zero, out _buffer);
                Fn<CreateUavDel>(_device, DEV_CREATE_UAV)(_device, _buffer, IntPtr.Zero, out _uav);   // default UAV over the whole buffer
                if (_shader == IntPtr.Zero || _uav == IntPtr.Zero)
                {
                    error = "Initialisation de la charge GPU échouée.";
                    Cleanup();
                    return false;
                }

                Fn<CsSetShaderDel>(_ctx, CTX_CS_SET_SHADER)(_ctx, _shader, IntPtr.Zero, 0);
                IntPtr uavLocal = _uav;
                Fn<CsSetUavDel>(_ctx, CTX_CS_SET_UAV)(_ctx, 0, 1, ref uavLocal, IntPtr.Zero);

                _run = true;
                _loop = new Thread(DispatchLoop) { IsBackground = true, Name = "GpuStressLoad" };
                _loop.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GPU stress load failed to start");
                error = "Le test de charge GPU n'a pas pu démarrer : " + ex.Message;
                Cleanup();
                return false;
            }
        }
    }

    private void DispatchLoop()
    {
        var dispatch = Fn<DispatchDel>(_ctx, CTX_DISPATCH);
        while (_run)
        {
            // A burst of bounded dispatches keeps the GPU saturated; the short sleep paces the CPU-side
            // submission so we never build an unbounded command backlog or trip TDR on our own work.
            for (int i = 0; i < 16 && _run; i++) dispatch(_ctx, Elements / 256, 1, 1);
            Thread.Sleep(1);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _run = false;
            try { _loop?.Join(2000); } catch { }
            _loop = null;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        // Release in reverse acquisition order. Immediate context/device last.
        if (_uav != IntPtr.Zero) { Marshal.Release(_uav); _uav = IntPtr.Zero; }
        if (_buffer != IntPtr.Zero) { Marshal.Release(_buffer); _buffer = IntPtr.Zero; }
        if (_shader != IntPtr.Zero) { Marshal.Release(_shader); _shader = IntPtr.Zero; }
        if (_ctx != IntPtr.Zero) { Marshal.Release(_ctx); _ctx = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
    }
}

/// <summary>Test double: records Start/Stop, never touches a GPU. Configurable availability.</summary>
public sealed class FakeGpuStressLoad : IGpuStressLoad
{
    public bool Available { get; set; } = true;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool IsRunning { get; private set; }

    public bool Start(out string error)
    {
        StartCount++;
        if (!Available) { error = "indisponible (simulé)"; return false; }
        error = string.Empty; IsRunning = true; return true;
    }

    public void Stop() { StopCount++; IsRunning = false; }
}
