using Omega.Plugins;

namespace OmegaStudio.Rendering;

/// <summary>One contiguous run of triangles sharing a texture — one draw call.</summary>
internal readonly record struct DrawBatch(int First, int Count, int Texture, bool AlphaClip);

/// <summary>
/// A scene packed into the interleaved vertex layout the GPU reads, plus the
/// expanded line list wireframe mode draws.
///
/// <para>
/// One packer for every format. The two it replaced were the same code twice over,
/// differing only in whether the source geometry was indexed: one walked an expanded
/// triangle stream, the other de-referenced an index buffer. <see cref="SceneMesh"/>
/// admits both — a null <see cref="SceneMesh.Indices"/> means the vertices are
/// already a triangle list — so the difference is one branch here rather than a
/// second copy of the whole walk.
/// </para>
///
/// <para>
/// Posing re-runs the same walk with different positions and normals, which is why
/// <see cref="Pack"/> takes them as parameters instead of there being a near
/// identical copy of the walk for animated frames.
/// </para>
/// </summary>
internal sealed class MeshBuffer
{
    /// <summary>Bytes per vertex: position 12 + normal 12 + uv 8 + colour 4.</summary>
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
        Lines = new byte[LineVertexCount * Stride];
    }

    public int VertexCount { get; }
    public int LineVertexCount { get; }
    public byte[] Vertices { get; }
    public byte[] Lines { get; }
    public IReadOnlyList<DrawBatch> Batches { get; }

    /// <summary>
    /// Lays out <paramref name="scene"/> and records which GL texture each run of
    /// triangles uses.
    ///
    /// <para>
    /// The batches depend only on the submesh division, so they are computed once
    /// here and stay valid for every subsequent pose.
    /// </para>
    /// </summary>
    /// <param name="scene">The geometry, already validated.</param>
    /// <param name="textures">GL texture handles, one per <see cref="SceneGeometry.Textures"/> entry.</param>
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

    /// <summary>
    /// Writes every vertex into <see cref="Vertices"/> and expands each triangle into
    /// three wireframe edges in <see cref="Lines"/>.
    ///
    /// <para>
    /// When <paramref name="posedPositions"/> is null the mesh's own rest arrays are
    /// used. Otherwise both arrays hold one entry per <i>source</i> vertex, mesh after
    /// mesh in the order this walk visits them — which is what
    /// <see cref="IAnimatedAsset.TryPose"/> is specified to produce.
    /// </para>
    /// </summary>
    public void Pack(SceneGeometry scene, float[]? posedPositions, float[]? posedNormals)
    {
        ArgumentNullException.ThrowIfNull(scene);

        bool posed = posedPositions is not null && posedNormals is not null;
        int vertex = 0;
        int line = 0;
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

                    // Three edges of the triangle just completed, as six line vertices.
                    if ((i + 1) % 3 == 0)
                    {
                        line = WriteTriangleEdges(vertex, line);
                    }
                }
            }

            meshBase += mesh.VertexCount * 3;
        }
    }

    private void WriteVertex(
        SceneMesh mesh, float[] positions, float[] normals, int shift, int source, int at)
    {
        // Bounds-checked, positions included: the arrays may be a plugin's posed frame rather
        // than the mesh's own, and a plugin that returns the wrong length should cost the frame
        // rather than throw out of the render loop.
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

    private int WriteTriangleEdges(int vertex, int line)
    {
        int a = (vertex - 3) * Stride;
        int b = (vertex - 2) * Stride;
        int c = (vertex - 1) * Stride;

        foreach (int edge in (ReadOnlySpan<int>)[a, b, b, c, c, a])
        {
            Vertices.AsSpan(edge, Stride).CopyTo(Lines.AsSpan(line));
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
