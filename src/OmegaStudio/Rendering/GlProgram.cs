using Avalonia.OpenGL;
using static Avalonia.OpenGL.GlConsts;
namespace OmegaStudio.Rendering;

internal sealed class GlProgram
{
    private GlProgram(int handle)
    {
        Handle = handle;
    }
    public int Handle { get; private set; }
    public static GlProgram Build(GlInterface gl, string header, string vertexSource, string fragmentSource, string name)
    {
        int vertex = Compile(gl, GL_VERTEX_SHADER, header + vertexSource, $"{name} vertex shader");
        int fragment = Compile(gl, GL_FRAGMENT_SHADER, header + fragmentSource, $"{name} fragment shader");
        int handle = gl.CreateProgram();
        gl.AttachShader(handle, vertex);
        gl.AttachShader(handle, fragment);
        Check(gl.LinkProgramAndGetError(handle), $"{name} program link");
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        return new GlProgram(handle);
    }
    public int Uniform(GlInterface gl, string name)
    {
        return gl.GetUniformLocationString(Handle, name);
    }
    public void Use(GlInterface gl)
    {
        gl.UseProgram(Handle);
    }
    public void Delete(GlInterface gl)
    {
        if (Handle != 0)
        {
            gl.DeleteProgram(Handle);
            Handle = 0;
        }
    }
    private static int Compile(GlInterface gl, int stage, string source, string what)
    {
        int shader = gl.CreateShader(stage);
        Check(gl.CompileShaderAndGetError(shader, source), what);
        return shader;
    }
    private static void Check(string? error, string what)
    {
        if (!string.IsNullOrEmpty(error))
        {
            throw new InvalidOperationException($"{what} failed: {error}");
        }
    }
}
