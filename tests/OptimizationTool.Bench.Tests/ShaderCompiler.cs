using System.Runtime.InteropServices;
using System.Text;

namespace OptimizationTool.Bench.Tests;

/// <summary>
/// Compiles the worker's HLSL with %SystemRoot%\System32\d3dcompiler_47.dll, the same way the embedded .cso files were
/// produced (cs_5_0, entry "main", OPTIMIZATION_LEVEL3 | IEEE_STRICTNESS). Test-side only: the worker never compiles.
/// </summary>
internal static unsafe partial class ShaderCompiler
{
    public const uint Flags = (1u << 15) | (1u << 13); // D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_IEEE_STRICTNESS

    [LibraryImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    private static partial int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target,
        uint flags1, uint flags2, void** code, void** errors);

    public static byte[] Compile(string hlslPath)
    {
        var src = File.ReadAllBytes(hlslPath);
        var name = Encoding.ASCII.GetBytes(Path.GetFileName(hlslPath) + "\0");
        var entry = "main\0"u8.ToArray();
        var target = "cs_5_0\0"u8.ToArray();
        void* code = null, errors = null;
        int hr;
        fixed (byte* s = src, n = name, e = entry, t = target)
            hr = D3DCompile(s, (nuint)src.Length, n, null, null, e, t, Flags, 0, &code, &errors);
        try
        {
            if (hr < 0)
            {
                var msg = errors is null ? "" : Marshal.PtrToStringAnsi((IntPtr)BlobPtr(errors), (int)BlobSize(errors));
                throw new InvalidOperationException($"D3DCompile 0x{hr:X8}: {msg}");
            }
            return new ReadOnlySpan<byte>(BlobPtr(code), (int)BlobSize(code)).ToArray();
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    // ID3DBlob vtable: 0 QueryInterface, 1 AddRef, 2 Release, 3 GetBufferPointer, 4 GetBufferSize.
    private static void* BlobPtr(void* blob) => ((delegate* unmanaged[Stdcall]<void*, void*>)(*(void***)blob)[3])(blob);
    private static nuint BlobSize(void* blob) => ((delegate* unmanaged[Stdcall]<void*, nuint>)(*(void***)blob)[4])(blob);
    private static void Release(void* blob)
    {
        if (blob is not null) ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)blob)[2])(blob);
    }
}
