using SceneGate.Ekona.Containers.Rom;
using Yarhl.FileSystem;
using Yarhl.IO;
namespace Nintendo.Formats;
internal sealed class NdsRom : IDisposable
{
    private readonly bool _uncompressedProgram;
    private NdsRom(Node root)
    {
        Root = root;
        Rom = root.GetFormatAs<NitroRom>()
            ?? throw new InvalidDataException("the file did not parse as a DS ROM image");
        _uncompressedProgram = Rom.Information.ProgramCodeParameters is null or { CompressedLength: 0 };
    }
    public NitroRom Rom { get; }
    public ProgramInfo Information => Rom.Information;
    public Banner? Banner => Rom.Banner;
    public Node Root { get; }
    public bool HasChanges { get; private set; }
    public bool HasDsiProgram =>
        Navigator.SearchNode(Root, $"/{Root.Name}/system/arm9i") is not null;
    public int CountFiles()
    {
        return Navigator.IterateNodes(Root, NavigationMode.BreadthFirst)
            .Count(node => !node.IsContainer);
    }
    public void MarkChanged()
    {
        HasChanges = true;
    }
    public static NdsRom Load(string path)
    {
        DataStream source = DataStreamFactory.FromTemporaryFile(deleteOnClose: true);
        Node? root = null;
        try
        {
            using (FileStream original = File.OpenRead(path))
            {
                original.CopyTo(source);
            }
            source.Position = 0;
            root = new Node(Path.GetFileName(path), new BinaryFormat(source));
            _ = root.TransformWith<Binary2NitroRom>();
            return new NdsRom(root);
        }
        catch
        {
            if (root is null)
            {
                source.Dispose();
            }
            else
            {
                root.Dispose();
            }
            throw;
        }
    }
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using DataStream scratch = DataStreamFactory.FromTemporaryFile(deleteOnClose: true);
        NitroRom2BinaryParams parameters = new()
        {
            OutputStream = scratch,
            DecompressedProgram = _uncompressedProgram,
        };
        using (BinaryFormat image = new NitroRom2Binary(parameters).Convert(Rom))
        {
            image.Stream.WriteTo(output);
        }
        output.Flush();
    }
    public void MarkSaved()
    {
        HasChanges = false;
    }
    public void Dispose()
    {
        Root.Dispose();
    }
}
