using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Omega.Plugins;
namespace OmegaStudio.Rendering;
internal sealed class GlUniforms : IDisposable
{
    private readonly GlInterface _gl;
    private readonly MethodInfo? _uniformMatrix4fv;
    private readonly Dictionary<int, object?[]> _argsByLocation = [];
    private readonly float[] _scratch = new float[16];
    private readonly IntPtr _scratchAddress;
    private GCHandle _pin;
    private bool _disposed;
    private GlUniforms(GlInterface gl, MethodInfo? uniformMatrix4fv)
    {
        _gl = gl;
        _uniformMatrix4fv = uniformMatrix4fv;
        _pin = GCHandle.Alloc(_scratch, GCHandleType.Pinned);
        _scratchAddress = _pin.AddrOfPinnedObject();
    }
    public static GlUniforms Create(GlInterface gl)
    {
        return new GlUniforms(
            gl,
            typeof(GlInterface).GetMethod(
                "UniformMatrix4fv", BindingFlags.Public | BindingFlags.Instance));
    }
    public void Matrix(int location, in Matrix4x4 m)
    {
        if (_uniformMatrix4fv is null || _disposed)
        {
            return;
        }
        MatrixOps.CopyTo(m, _scratch);
        if (!_argsByLocation.TryGetValue(location, out object?[]? args))
        {
            args = [location, 1, false, _scratchAddress];
            _argsByLocation[location] = args;
        }
        _ = _uniformMatrix4fv.Invoke(_gl, args);
    }
    ~GlUniforms()
    {
        Release();
    }
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }
    private void Release()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }
    }
}
