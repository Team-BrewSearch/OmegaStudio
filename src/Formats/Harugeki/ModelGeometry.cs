namespace Harugeki.Formats;
public sealed class ModelGeometry
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] Uvs { get; init; }
    public required byte[] Colors { get; init; }
    public required IReadOnlyList<int[]> MaterialIndices { get; init; }
    public required IReadOnlyDictionary<int, (int[] Vertices, float[] Weights)> BoneWeights { get; init; }
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => MaterialIndices.Sum(m => m.Length) / 3;
    public void Validate(string what)
    {
        int vertices = VertexCount;
        if (vertices == 0 || TriangleCount == 0)
        {
            throw new InvalidDataException($"{what}: no geometry to write.");
        }
        if (vertices > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"{what}: {vertices} vertices exceeds the format's limit of {ushort.MaxValue} " +
                "— the draw stream stores vertex indices as 16-bit. Split the mesh or decimate it.");
        }
        Check(Positions.Length, vertices * 3, "positions");
        Check(Normals.Length, vertices * 3, "normals");
        Check(Uvs.Length, vertices * 2, "UVs");
        Check(Colors.Length, vertices * 4, "colours");
        if (MaterialIndices.Count == 0)
        {
            throw new InvalidDataException($"{what}: no material groups.");
        }
        foreach (int[] group in MaterialIndices)
        {
            if (group.Length % 3 != 0)
            {
                throw new InvalidDataException($"{what}: a material group has {group.Length} indices, not a multiple of three.");
            }
            foreach (int index in group)
            {
                if (index < 0 || index >= vertices)
                {
                    throw new InvalidDataException(
                        $"{what}: triangle index {index} is outside the {vertices}-vertex array.");
                }
            }
        }
        foreach ((int bone, (int[] vertices2, float[] weights)) in BoneWeights)
        {
            if (vertices2.Length != weights.Length)
            {
                throw new InvalidDataException($"{what}: bone {bone} has {vertices2.Length} vertices but {weights.Length} weights.");
            }
            foreach (int index in vertices2)
            {
                if (index < 0 || index >= vertices)
                {
                    throw new InvalidDataException(
                        $"{what}: bone {bone} weights vertex {index}, outside the {vertices}-vertex array.");
                }
            }
        }
        void Check(int actual, int expected, string field)
        {
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"{what}: {field} has {actual} values but {vertices} vertices need {expected}.");
            }
        }
    }
}
