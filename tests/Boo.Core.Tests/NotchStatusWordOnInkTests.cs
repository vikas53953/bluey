using Boo.Core;

namespace Boo.Core.Tests;

// Added after the independent review of PR #190: the word must sit on ink, not hang over the blot's edge. The first
// version measured the ink with LivingInk.BottomUnder, whose ±10 % band reaches the blot's top-right corner at the end
// of the slot and so reported the corner, not the ink under the word; this version asks the blot itself (InkBlot.Contains)
// for every sample of the word's 18 DIP line, which is the question the review raised.
public sealed class NotchStatusWordOnInkTests
{
    private static NotchFaceSpec D => FaceSpecFixture.Load().Face;

    [Theory]
    [InlineData(NotchFaceState.Idle, 38)]
    [InlineData(NotchFaceState.Hover, 38)]
    [InlineData(NotchFaceState.Idle, 46)]
    [InlineData(NotchFaceState.Listening, 62)]
    [InlineData(NotchFaceState.Thinking, 64)]
    public void The_whole_word_line_lies_inside_the_ink(NotchFaceState state, double wordWidth)
    {
        var face = D;
        var body = NotchStatusWord.Fit(face, state, wordWidth);
        var row = LivingInk.Place(face, body, state, wordWidth);
        var blot = LivingInk.BlotFor(state);
        var lineTop = row.LeftEye.Y - 9;
        var lineBottom = row.LeftEye.Y + 9;
        var outside = new List<string>();
        for (var i = 0; i <= 20; i++)
        {
            var x = row.Trail.X + row.Trail.Width * i / 20;
            foreach (var y in new[] { lineTop, row.LeftEye.Y, lineBottom })
            {
                if (!blot.Contains(new PointD(x, y), body))
                {
                    outside.Add($"({x:0.0}, {y:0.0})");
                }
            }
        }
        Assert.True(outside.Count == 0, $"{state} (word {wordWidth} DIP, body {body.Width}×{body.Height}): off the ink at {string.Join(" ", outside)}");
        Assert.True(row.Trail.Width >= wordWidth, "the slot is narrower than the word");
        Assert.True(row.Trail.X + row.Trail.Width <= body.Width - LivingInk.SidePadding + 1e-6, "the slot runs into the right padding");
    }

    [Theory]
    [InlineData(NotchFaceState.Idle, 38)]
    [InlineData(NotchFaceState.Listening, 62)]
    [InlineData(NotchFaceState.Thinking, 64)]
    public void The_word_never_touches_the_eyes(NotchFaceState state, double wordWidth)
    {
        var face = D;
        var body = NotchStatusWord.Fit(face, state, wordWidth);
        var row = LivingInk.Place(face, body, state, wordWidth);
        var rightEyeEdge = row.RightEye.X + face.Eye.Width / 2;
        Assert.True(row.Trail.X - rightEyeEdge >= LivingInk.SlotGap - 1e-6,
            $"{state}: only {row.Trail.X - rightEyeEdge:0.0} DIP between the right eye and the word (want {LivingInk.SlotGap})");
    }
}
