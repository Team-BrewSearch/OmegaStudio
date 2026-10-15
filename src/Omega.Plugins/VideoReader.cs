namespace Omega.Plugins;
public sealed record VideoInfo
{
    public VideoInfo(int width, int height, int rateNumerator, int rateDenominator, int frameCount, string codec)
    {
        if (width <= 0 || width > ImageData.MaxDimension || height <= 0 || height > ImageData.MaxDimension)
        {
            throw new ArgumentException(
                $"Video dimensions {width}x{height} are outside the supported range (1-{ImageData.MaxDimension}).");
        }
        if (rateNumerator <= 0 || rateDenominator <= 0)
        {
            throw new ArgumentException($"Frame rate {rateNumerator}/{rateDenominator} must be positive.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        Width = width;
        Height = height;
        RateNumerator = rateNumerator;
        RateDenominator = rateDenominator;
        FrameCount = frameCount;
        Codec = codec ?? string.Empty;
    }
    public int Width { get; }
    public int Height { get; }
    public int RateNumerator { get; }
    public int RateDenominator { get; }
    public int FrameCount { get; }
    public string Codec { get; }
    public double FramesPerSecond => (double)RateNumerator / RateDenominator;
    public int FrameBytes => Width * Height * 4;
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / FramesPerSecond);
    public TimeSpan TimeOf(int frame)
    {
        return TimeSpan.FromSeconds(frame / FramesPerSecond);
    }
    public int FrameAt(TimeSpan time)
    {
        return (int)Math.Clamp(Math.Floor(time.TotalSeconds * FramesPerSecond + 1e-9), 0, Math.Max(0, FrameCount - 1));
    }
}
public interface IVideoReader : IDisposable
{
    VideoInfo Info { get; }
    int Position { get; }
    void Seek(int frame);
    bool ReadFrame(Span<byte> rgba);
}
