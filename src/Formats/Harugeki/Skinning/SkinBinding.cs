namespace Harugeki.Formats.Skinning;

public sealed class SkinBinding
{
    public const int MaxInfluences = 4;
    private readonly int[] _joints;
    private readonly float[] _weights;
    private SkinBinding(int skinVertexCount)
    {
        SkinVertexCount = skinVertexCount;
        _joints = new int[skinVertexCount * MaxInfluences];
        _weights = new float[skinVertexCount * MaxInfluences];
    }
    public int SkinVertexCount { get; }
    public static SkinBinding? Build(RingModel model, RingMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(mesh);
        int skinVertexCount = mesh.SkinPositions.Length / 3;
        if (skinVertexCount == 0)
        {
            return null;
        }
        SkinBinding binding = new(skinVertexCount);
        bool any = false;
        for (int jointIndex = 0; jointIndex < model.Bones.Count; jointIndex++)
        {
            RingBone bone = model.Bones[jointIndex];
            if (bone.MeshIndex != mesh.NodeIndex)
            {
                continue;
            }
            for (int k = 0; k < bone.Weights.Length; k++)
            {
                int vertex = bone.WeightVertices[k];
                float weight = bone.Weights[k];
                if (vertex < 0 || vertex >= skinVertexCount || weight <= 0f)
                {
                    continue;
                }
                binding.Insert(vertex, weight, jointIndex);
                any = true;
            }
        }
        if (!any)
        {
            return null;
        }
        binding.Normalise();
        return binding;
    }
    public bool TryGetInfluences(int skinVertex, out ReadOnlySpan<int> joints, out ReadOnlySpan<float> weights)
    {
        if (skinVertex < 0 || skinVertex >= SkinVertexCount)
        {
            joints = default;
            weights = default;
            return false;
        }
        int at = skinVertex * MaxInfluences;
        joints = _joints.AsSpan(at, MaxInfluences);
        weights = _weights.AsSpan(at, MaxInfluences);
        return weights[0] > 0f;
    }
    private void Insert(int vertex, float weight, int joint)
    {
        int at = vertex * MaxInfluences;
        for (int slot = 0; slot < MaxInfluences; slot++)
        {
            if (weight <= _weights[at + slot])
            {
                continue;
            }
            for (int shift = MaxInfluences - 1; shift > slot; shift--)
            {
                _weights[at + shift] = _weights[at + shift - 1];
                _joints[at + shift] = _joints[at + shift - 1];
            }
            _weights[at + slot] = weight;
            _joints[at + slot] = joint;
            return;
        }
    }
    private void Normalise()
    {
        for (int at = 0; at < _weights.Length; at += MaxInfluences)
        {
            float sum = 0f;
            for (int k = 0; k < MaxInfluences; k++)
            {
                sum += _weights[at + k];
            }
            if (sum is 0f or 1f)
            {
                continue;
            }
            for (int k = 0; k < MaxInfluences; k++)
            {
                _weights[at + k] /= sum;
            }
        }
    }
}
