using Omega.Plugins;
namespace OmegaStudio.Rendering;
internal readonly record struct DrawBatch(int First, int Count, int Texture, bool AlphaClip);
internal sealed class MeshBuffer
{
    public const int PoseStride = 12;
    public const int SurfaceStride = 12;
    public const int ColorOffset = 8;
    private static readonly byte[] s_white = [255, 255, 255, 255];
    private MeshBuffer(SceneGeometry scene, int indexCount, List<DrawBatch> batches)
    {
        Scene = scene;
        VertexCount = scene.VertexCount;
        IndexCount = indexCount;
        Batches = batches;
    }
    public SceneGeometry Scene { get; }
    public int VertexCount { get; }
    public int IndexCount { get; }
    public int LineIndexCount => checked(IndexCount * 2);
    public int NormalOffset => checked(VertexCount * PoseStride);
    public IReadOnlyList<DrawBatch> Batches { get; }
    public static MeshBuffer Build(SceneGeometry scene, IReadOnlyList<int> textures)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(textures);
        List<DrawBatch> batches = [];
        int index = 0;
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
                batches.Add(new DrawBatch(index, submesh.Count, texture, submesh.AlphaClip));
                index += submesh.Count;
            }
        }
        return new MeshBuffer(scene, index, batches);
    }
    public int[] Indices(bool lines)
    {
        int[] indices = new int[lines ? LineIndexCount : IndexCount];
        int at = 0;
        int first = 0;
        foreach (SceneMesh mesh in Scene.Meshes)
        {
            foreach (SceneSubmesh submesh in mesh.Submeshes)
            {
                for (int i = submesh.First; i + 3 <= submesh.First + submesh.Count; i += 3)
                {
                    int a = first + Source(mesh, i);
                    int b = first + Source(mesh, i + 1);
                    int c = first + Source(mesh, i + 2);
                    ReadOnlySpan<int> written = lines ? [a, b, b, c, c, a] : [a, b, c];
                    written.CopyTo(indices.AsSpan(at));
                    at += written.Length;
                }
            }
            first += mesh.VertexCount;
        }
        return indices;
    }
    public byte[] Surface()
    {
        byte[] surface = new byte[checked(VertexCount * SurfaceStride)];
        int at = 0;
        foreach (SceneMesh mesh in Scene.Meshes)
        {
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++, at += SurfaceStride)
            {
                if (mesh.Uvs.Length >= (vertex * 2) + 2)
                {
                    Span<byte> uv = surface.AsSpan(at);
                    _ = BitConverter.TryWriteBytes(uv, mesh.Uvs[vertex * 2]);
                    _ = BitConverter.TryWriteBytes(uv[4..], mesh.Uvs[(vertex * 2) + 1]);
                }
                ReadOnlySpan<byte> colour = mesh.Colors.Length >= (vertex * 4) + 4
                    ? mesh.Colors.AsSpan(vertex * 4, 4)
                    : s_white;
                colour.CopyTo(surface.AsSpan(at + ColorOffset, 4));
            }
        }
        return surface;
    }
    private static int Source(SceneMesh mesh, int entry)
    {
        return mesh.Indices is { } indices ? indices[entry] : entry;
    }
}
