using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
namespace OmegaStudio.Views;
public sealed class VideoView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<VideoView, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<VideoView, int>(nameof(Version));
    static VideoView()
    {
        AffectsRender<VideoView>(SourceProperty, VersionProperty);
    }
    public VideoView()
    {
        ClipToBounds = true;
    }
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (Source is not { } image || image.Size.Width <= 0 || image.Size.Height <= 0)
        {
            return;
        }
        double scale = Math.Min(Bounds.Width / image.Size.Width, Bounds.Height / image.Size.Height);
        Size size = image.Size * scale;
        Rect destination = new(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(image, new Rect(image.Size), destination);
        }
    }
}
