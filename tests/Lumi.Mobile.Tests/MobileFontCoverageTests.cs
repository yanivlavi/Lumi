using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileFontCoverageTests
{
    [Fact]
    public async Task BundledHebrewFallbackContainsTheHebrewAlphabet()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new Uri("avares://Lumi.Mobile/Assets/Fonts/NotoSansHebrew-Regular.ttf"));
                using var typeface = SKTypeface.FromStream(stream, 0);

                Assert.NotNull(typeface);
                Assert.Equal(400, typeface.FontWeight);
                foreach (var character in "אבגדהוזחטיכלמנסעפצקרשת")
                    Assert.NotEqual(0, typeface.GetGlyph(character));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            throw failure;
    }

    [Fact]
    public async Task BundledEmojiFallbackContainsRepresentativeEmojiSequences()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new Uri("avares://Lumi.Mobile/Assets/Fonts/NotoColorEmoji-Regular.ttf"));
                using var typeface = SKTypeface.FromStream(stream, 0);

                Assert.NotNull(typeface);
                Assert.Equal(400, typeface.FontWeight);
                foreach (var codepoint in new[]
                         {
                             0x1F44B, // Waving hand
                             0x1F600, // Grinning face
                             0x2764,  // Heart
                             0x2600,  // Sun
                             0x1F3FB, // Skin tone modifier
                             0x1F1FA, // Regional indicator U
                             0x1F1F8, // Regional indicator S
                             0x200D,  // Zero-width joiner
                             0x20E3   // Combining enclosing keycap
                         })
                {
                    Assert.NotEqual(0, typeface.GetGlyph(codepoint));
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            throw failure;
    }
}
