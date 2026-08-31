using Avalonia.OpenGL;
using Omega.Plugins;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OmegaStudio.Rendering;

/// <summary>
/// Uploads a 4x4 matrix uniform — the one setter Avalonia's
/// <see cref="GlInterface"/> exposes only by reflection.
///
/// <para>
/// It has to be reflection rather than a bound delegate. The parameter is
/// declared <c>void*</c>, and <see cref="MethodInfo.CreateDelegate{T}(object)"/>
/// demands an exact signature match, so binding it needs a delegate with a
/// pointer parameter — which needs an unsafe context. Reflection accepts an
/// <see cref="IntPtr"/> for that parameter quite happily and keeps the project
/// safe-only.
/// </para>
///
/// <para>
/// The cost is nothing worth optimising: this runs about four times a frame, and
/// the argument arrays below are built once per uniform location and then reused,
/// so the steady state allocates nothing at all. An earlier attempt to shave it
/// with a bound delegate is what silently blanked the entire viewport, because
/// the signature mismatch threw out of <c>OnOpenGlInit</c>.
/// </para>
/// </summary>
internal sealed class GlUniforms : IDisposable
{
    private readonly GlInterface _gl;
    private readonly MethodInfo? _uniformMatrix4fv;

    /// <summary>
    /// One ready-made argument array per uniform location. The scratch buffer's
    /// *address* never changes once pinned — only its contents — so every argument
    /// is constant per location and nothing needs boxing on the hot path.
    /// </summary>
    private readonly Dictionary<int, object?[]> _argsByLocation = [];

    private readonly float[] _scratch = new float[16];
    private readonly IntPtr _scratchAddress;

    /// <summary>
    /// Not readonly: <see cref="GCHandle.Free"/> mutates the struct, and calling it on a
    /// readonly field would free through a defensive copy — leaving this one still
    /// reporting <see cref="GCHandle.IsAllocated"/> and throwing on a second Free.
    /// </summary>
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

    /// <summary>
    /// Uploads a matrix untransposed.
    ///
    /// <para>
    /// <see cref="Matrix4x4"/> is row-major (the D3D row-vector convention), so
    /// leaving <c>transpose</c> false makes GLSL read the transpose — which is
    /// exactly what makes the shader's column-vector product equal the row-vector
    /// product used to build it.
    /// </para>
    /// </summary>
    public void Matrix(int location, in Matrix4x4 m)
    {
        // Once disposed the pinned address is stale, so an upload here would hand the
        // driver freed memory. The viewport can still hold a reference across a device
        // loss, so this is checked rather than assumed.
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

    /// <summary>
    /// The backstop for the pinned handle. A pin is not collectable state: if the GL
    /// control is torn down without <c>OnOpenGlDeinit</c>
    /// running — a lost device, a throw during teardown — nothing else ever frees it,
    /// and the scratch array stays pinned in gen 0 for the life of the process.
    /// </summary>
    ~GlUniforms()
    {
        Release();
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Frees the pin, once. Touches nothing managed, so it is safe from the finalizer.
    /// </summary>
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
