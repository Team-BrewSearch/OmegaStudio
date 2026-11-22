using Avalonia.OpenGL;
namespace OmegaStudio.Rendering;
internal static class Shaders
{
    public static string Header(GlVersion version)
    {
        return version.Type == GlProfileType.OpenGLES
            ? "#version 300 es\nprecision highp float;\n"
            : "#version 330 core\n";
    }
    public const string ModelVertex = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aColor;
        uniform mat4 uMvp;
        uniform mat4 uView;
        out vec3 vNormal;
        out vec3 vViewPos;
        out vec2 vUv;
        out vec4 vColor;
        void main()
        {
            vNormal = mat3(uView) * aNormal;
            vViewPos = (uView * vec4(aPos, 1.0)).xyz;
            vUv = aUv;
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;
    public const string ModelFragment = """
        uniform sampler2D uTex;
        uniform int uMode;
        uniform float uHasTex;
        uniform float uAlphaClip;
        uniform float uVertexLit;
        uniform float uBlend;
        in vec3 vNormal;
        in vec3 vViewPos;
        in vec2 vUv;
        in vec4 vColor;
        out vec4 fragColor;
        void main()
        {
            // meshes without normals get the camera-facing face normal; derivatives stay outside the branch
            vec3 face = cross(dFdx(vViewPos), dFdy(vViewPos));
            vec3 n = normalize(vNormal);
            if (dot(vNormal, vNormal) < 1e-8) n = normalize(face);
            else if (!gl_FrontFacing) n = -n;
            float lambert = 0.28 + 0.72 * max(n.z, 0.0);
            if (uMode == 3) { fragColor = vec4(n * 0.5 + 0.5, 1.0); return; }
            if (uMode == 4) { fragColor = vec4(fract(vUv), 0.0, 1.0); return; }
            if (uMode == 2) { fragColor = vec4(0.55, 0.85, 1.0, 1.0); return; }
            vec4 base = uHasTex > 0.5 ? texture(uTex, vUv) : vec4(0.78, 0.78, 0.80, 1.0);
            if (uAlphaClip > 0.5 && base.a < 0.5) discard;
            float alpha = uBlend > 0.5 ? base.a * vColor.a : 1.0;
            if (alpha <= 0.0) discard;
            if (uMode == 1) base = vec4(0.78, 0.78, 0.80, 1.0);
            vec3 light = uVertexLit > 0.5 ? vColor.rgb : vec3(lambert);
            fragColor = vec4(base.rgb * light, alpha);
        }
        """;
    public const string FlatVertex = """
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;
    public const string FlatFragment = """
        uniform float uR;
        uniform float uG;
        uniform float uB;
        out vec4 fragColor;
        void main() { fragColor = vec4(uR, uG, uB, 1.0); }
        """;
    public const string VertexColorVertex = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main() { vColor = aColor; gl_Position = uMvp * vec4(aPos, 1.0); }
        """;
    public const string VertexColorFragment = """
        in vec3 vColor;
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;
}
