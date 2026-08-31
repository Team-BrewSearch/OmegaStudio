namespace Omega.Plugins;

/// <summary>
/// Recognises a standard payload inside somebody else's archive.
///
/// <para>
/// A container plugin should not have to know what an OGG file looks like: it hands
/// over leaf nodes, and a recogniser says what the bytes are. That is what lets a WAV
/// get a transport in every format at once, and why adding a viewer for a new standard
/// format never touches a game plugin.
/// </para>
///
/// <para>
/// The host takes the first recogniser to answer, and only offers nodes that hold
/// bytes and claim no content capability of their own — so a plugin that has already
/// decoded something is never second-guessed.
/// </para>
/// </summary>
public interface IAssetRecognizer
{
    /// <summary>Stable identifier, for diagnostics. e.g. <c>generic.audio</c>.</summary>
    string Id { get; }

    /// <summary>
    /// Whether these bytes are content this recogniser knows. Asked once per row while
    /// a folder expands, so check a magic number and return.
    ///
    /// <para>
    /// Separate from <see cref="Wrap"/> because it also answers
    /// <see cref="IHostServices.IsKnownPayload"/>, where a container walk needs the
    /// question settled without anything being built.
    /// </para>
    /// </summary>
    bool Claims(ReadOnlySpan<byte> data);

    /// <summary>
    /// A view of <paramref name="node"/> carrying the capabilities its content implies.
    /// Only called once <see cref="Claims"/> has said yes.
    ///
    /// <para>
    /// Anything expensive belongs behind the capability the view exposes, not here:
    /// this runs while a folder expands, and the host only reads a capability on
    /// selection.
    /// </para>
    /// </summary>
    IAssetNode Wrap(IAssetNode node, IHostServices host);
}

/// <summary>
/// The base a recognised view derives from: it forwards everything to the node it
/// wraps, leaving the view to add one capability and, usually, a kind.
/// </summary>
/// <example>
/// <code>
/// internal sealed class AudioView(IAssetNode inner) : RecognizedAsset(inner), IAudioAsset
/// {
///     public override string Kind =&gt; AssetKinds.Audio;
///     public ReadOnlySpan&lt;byte&gt; EncodedAudio =&gt; Data;
/// }
/// </code>
/// </example>
/// <remarks>
/// Forwarding <see cref="IDescribable"/> and <see cref="ICommandSource"/> is what stops
/// a wrapped node losing its Properties rows or its plugin's own commands. The content
/// capabilities are deliberately not forwarded: the host never offers a node that has
/// one, so there is nothing to forward.
/// <para>
/// One consequence for command authors — a command listed by a wrapped node receives
/// the <i>wrapper</i> as <see cref="AssetCommandContext.Node"/>. Capture what you need
/// when you build the command rather than casting that back to your own type.
/// </para>
/// </remarks>
public abstract class RecognizedAsset : IAssetNode, IBinaryAsset, IDescribable, ICommandSource
{
    private readonly IBinaryAsset _bytes;

    /// <param name="inner">
    /// The node being wrapped. Must hold bytes: recognition only ever runs on one that
    /// does, and the view needs them to answer <see cref="Data"/>.
    /// </param>
    protected RecognizedAsset(IAssetNode inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        Inner = inner;
        _bytes = inner as IBinaryAsset
            ?? throw new ArgumentException("a recognised node must hold bytes", nameof(inner));
    }

    /// <summary>The node this view wraps.</summary>
    public IAssetNode Inner { get; }

    /// <summary>The wrapped node's label.</summary>
    public string Name => Inner.Name;

    /// <summary>The wrapped node's grey text.</summary>
    public string Detail => Inner.Detail;

    /// <summary>The wrapped node's identifier.</summary>
    public string PathText => Inner.PathText;

    /// <summary>Override to state what the recognised content is.</summary>
    public virtual string Kind => Inner.Kind;

    /// <summary>Whether the wrapped node has children.</summary>
    public bool HasChildren => Inner.HasChildren;

    /// <summary>The wrapped node's children, untouched.</summary>
    public IEnumerable<IAssetNode> GetChildren()
    {
        return Inner.GetChildren();
    }

    /// <summary>The payload's size.</summary>
    public int Size => _bytes.Size;

    /// <summary>The payload, without copying.</summary>
    public ReadOnlySpan<byte> Data => _bytes.Data;

    /// <summary>Override to name the extension the content actually is.</summary>
    public virtual string Extension => _bytes.Extension;

    /// <summary>Whether the wrapped node allows its bytes to be replaced.</summary>
    public bool CanReplaceBytes => _bytes.CanReplaceBytes;

    /// <summary>
    /// Replaces the wrapped node's bytes. The host re-runs recognition afterwards, so a
    /// payload that is no longer what it was gets a different view.
    /// </summary>
    public void ReplaceBytes(byte[] payload)
    {
        _bytes.ReplaceBytes(payload);
    }

    /// <summary>The wrapped node's rows. Override to add what the content is, then call base.</summary>
    public virtual IEnumerable<PropertyRow> Describe()
    {
        return Inner is IDescribable describable ? describable.Describe() : [];
    }

    /// <summary>The wrapped node's own commands, so wrapping costs it nothing.</summary>
    public virtual IEnumerable<AssetCommand> GetCommands()
    {
        return Inner is ICommandSource source ? source.GetCommands() : [];
    }

    /// <summary>Defers to the wrapped node, for diagnostics.</summary>
    public override string ToString()
    {
        return Inner.ToString() ?? Name;
    }
}
