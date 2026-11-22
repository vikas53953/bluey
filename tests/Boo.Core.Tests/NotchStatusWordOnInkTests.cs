using Boo.Core;

namespace Boo.Core.Tests;

// Added after the independent review of PR #190: the word must sit on ink, not hang over the blot's lower edge.
public sealed class NotchStatusWordOnInkTests
{
    private static NotchFaceSpec D => FaceSpecFixture.Load().Face;

    [Theory]
    [InlineData(NotchFaceState.Idle, 38)]
    [InlineData(NotchFaceState.Hover, 38)]
    [InlineData(NotchFaceState.Idle, 46)]
    [InlineData(NotchFaceState.Listening, 62)]
    [InlineData(NotchFaceState.Thinking, 64)]
    public void The_word_line_stays_above_the_blots_lower_edge_under_the_whole_slot(NotchFaceState state, double wordWidth)
    {
        var face = D;
        var body = NotchStatusWord.Fit(face, state, wordWidth);
        var row = LivingInk.Place(face, body, state, wordWidth);
        var blot = LivingInk.BlotFor(state);
        var lineBottom = row.LeftEye.Y + 9;
        foreach (var share in new[] { row.Trail.X / body.Width, (row.Trail.X + row.Trail.Width / 2) / body.Width, (row.Trail.X + row.Trail.Width) / body.Width })
        {
            var inkBottom = LivingInk.BottomUnder(blot, body, share);
            Assert.True(inkBottom >= lineBottom,
                $"{state}: at {share:0.00} of the width the ink ends at {inkBottom:0.0} DIP but the word line reaches {lineBottom:0.0}");
        }
        Assert.True(row.Trail.X + row.Trail.Width <= body.Width - LivingInk.SidePadding + 1e-6, "the slot runs into the right padding");
    }
}
