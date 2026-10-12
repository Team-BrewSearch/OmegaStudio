using Omega.Plugins;
namespace OmegaStudio.Rendering;
internal readonly record struct DrawBatch(int First, int Count, int Texture, bool AlphaClip);
internal sealed class MeshBuffer
{
    public const int Stride = 36;
    private const int NormalOffset = 12;
    private const int UvOffset = 24;
    private const int ColorOffset = 32;
    private static readonly byte[] s_white = [255, 255, 255, 255];
    private MeshBuffer(int vertexCount, List<DrawBatch> batches)
    {
        VertexCount = vertexCount;
        LineVertexCount = vertexCount * 2;
        Batches = batches;
        Vertices = new byte[vertexCount * Stride];
    }
    public int VertexCount { get; }
    public int LineVertexCount { get; }
    public byte[] Vertices { get; }
    public byte[]? Lines { get; private set; }
    public void EnsureLines()
    {
        if (Lines is not null)
        {
            return;
        }
        Lines = new byte[checked(LineVertexCount * Stride)];
    }
    public IReadOnlyList<DrawBatch> Batches { get; }
    public static MeshBuffer Build(SceneGeometry scene, IReadOnlyList<int> textures)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(textures);
        List<DrawBatch> batches = [];
        int vertex = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            foreach (SceneSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Count == 0)
                {
                    continue;
                }
                int texture = submesh.TextureIndex >= 0 && submesh.TextureIndex < textures.Count
                    ? textures[submesh.TextureIndex]
                    : 0;
                batches.Add(new DrawBatch(vertex, submesh.Count, texture, submesh.AlphaClip));
                vertex += submesh.Count;
            }
        }
        MeshBuffer buffer = new(vertex, batches);
        buffer.Pack(scene, null, null);
        return buffer;
    }
    public void Pack(SceneGeometry scene, float[]? posedPositions, float[]? posedNormals)
    {
        ArgumentNullException.ThrowIfNull(scene);
        bool posed = posedPositions is not null && posedNormals is not null;
        int vertex = 0;
        int line = 0;
        byte[]? lines = Lines;
        int meshBase = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            float[] positions = posed ? posedPositions! : mesh.Positions;
            float[] normals = posed ? posedNormals! : mesh.Normals;
            int shift = posed ? meshBase : 0;
            foreach (SceneSubmesh submesh in mesh.Submeshes)
            {
                for (int i = 0; i < submesh.Count; i++)
                {
                    int source = mesh.Indices is { } indices
                        ? indices[submesh.First + i]
                        : submesh.First + i;
                    WriteVertex(mesh, positions, normals, shift, source, vertex * Stride);
                    vertex++;
                    if (lines is not null && (i + 1) % 3 == 0)
                    {
                        line = WriteTriangleEdges(lines, vertex, line);
                    }
                }
            }
            meshBase += mesh.VertexCount * 3;
        }
    }
    private void WriteVertex(
        SceneMesh mesh, float[] positions, float[] normals, int shift, int source, int at)
    {
        int from = shift + (source * 3);
        if (from >= 0 && from + 3 <= positions.Length)
        {
            Write3(Vertices, at, positions, from);
        }
        if (from >= 0 && from + 3 <= normals.Length)
        {
            Write3(Vertices, at + NormalOffset, normals, from);
        }
        if (mesh.Uvs.Length >= (source * 2) + 2)
        {
            Span<byte> uv = Vertices.AsSpan(at + UvOffset);
            _ = BitConverter.TryWriteBytes(uv, mesh.Uvs[source * 2]);
            _ = BitConverter.TryWriteBytes(uv[4..], mesh.Uvs[(source * 2) + 1]);
        }
        ReadOnlySpan<byte> colour = mesh.Colors.Length >= (source * 4) + 4
            ? mesh.Colors.AsSpan(source * 4, 4)
            : s_white;
        colour.CopyTo(Vertices.AsSpan(at + ColorOffset, 4));
    }
    private int WriteTriangleEdges(byte[] lines, int vertex, int line)
    {
        int a = (vertex - 3) * Stride;
        int b = (vertex - 2) * Stride;
        int c = (vertex - 1) * Stride;
        foreach (int edge in (ReadOnlySpan<int>)[a, b, b, c, c, a])
        {
            Vertices.AsSpan(edge, Stride).CopyTo(lines.AsSpan(line));
            line += Stride;
        }
        return line;
    }
    private static void Write3(byte[] destination, int at, float[] source, int from)
    {
        Span<byte> span = destination.AsSpan(at);
        _ = BitConverter.TryWriteBytes(span, source[from]);
        _ = BitConverter.TryWriteBytes(span[4..], source[from + 1]);
        _ = BitConverter.TryWriteBytes(span[8..], source[from + 2]);
    }
}
