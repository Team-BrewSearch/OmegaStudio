using System.Numerics;
using Avalonia.OpenGL;
using Omega.Plugins;
namespace OmegaStudio.Rendering;
internal sealed class GlUniforms
{
    private readonly UniformMatrix4fvProc? _uniformMatrix4fv;
    private readonly float[] _scratch = new float[16];
    private GlUniforms(UniformMatrix4fvProc? uniformMatrix4fv)
    {
        _uniformMatrix4fv = uniformMatrix4fv;
    }
    private delegate void UniformMatrix4fvProc(int location, int count, byte transpose, float[] value);
    public static GlUniforms Create(GlInterface gl)
    {
        return new GlUniforms(Gl.Resolve<UniformMatrix4fvProc>(gl, "glUniformMatrix4fv"));
    }
    public void Matrix(int location, in Matrix4x4 m)
    {
        if (_uniformMatrix4fv is null)
        {
            return;
        }
        MatrixOps.CopyTo(m, _scratch);
        _uniformMatrix4fv(location, 1, 0, _scratch);
    }
}
