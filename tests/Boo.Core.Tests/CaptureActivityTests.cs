using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

public sealed class CaptureActivityTests
{
    // CA-001
    [Fact]
    public void Newly_created_operation_is_inactive()
    {
        var tracker = new CaptureActivityTracker();
        using var operation = tracker.CreateOperation();

        Assert.Empty(tracker.ActiveDisplays);
    }

    // CA-002
    [Fact]
    public void Start_activates_one_display_and_dispose_removes_that_activity()
    {
        var tracker = new CaptureActivityTracker();
        using var operation = tracker.CreateOperation();
        var suppliedDisplay = Display("display-1", "Captured display");
        var expectedDisplay = new DisplayInfo("display-1", "Captured display", new RectD(0, 0, 1920, 1080), 1);

        operation.Start(suppliedDisplay);

        Assert.Equal(new[] { expectedDisplay }, tracker.ActiveDisplays);

        operation.Dispose();

        Assert.Empty(tracker.ActiveDisplays);
    }

    // CA-003
    [Fact]
    public void Dispose_is_idempotent()
    {
        var tracker = new CaptureActivityTracker();
        using var operation = tracker.CreateOperation();
        operation.Start(Display("display-1"));

        operation.Dispose();
        operation.Dispose();

        Assert.Empty(tracker.ActiveDisplays);
    }

    // CA-004
    [Fact]
    public void Multiple_operations_on_one_display_remain_active_until_all_complete()
    {
        var tracker = new CaptureActivityTracker();
        using var first = tracker.CreateOperation();
        using var second = tracker.CreateOperation();
        var firstDisplay = Display("display-1", "Shared display");
        var secondDisplay = new DisplayInfo("display-1", "Shared display", new RectD(0, 0, 1920, 1080), 1);
        var expectedDisplay = new DisplayInfo("display-1", "Shared display", new RectD(0, 0, 1920, 1080), 1);

        first.Start(firstDisplay);
        second.Start(secondDisplay);

        Assert.Equal(new[] { expectedDisplay }, tracker.ActiveDisplays);

        first.Dispose();
        Assert.Equal(new[] { expectedDisplay }, tracker.ActiveDisplays);

        second.Dispose();
        Assert.Empty(tracker.ActiveDisplays);
    }

    // CA-005
    [Fact]
    public void Distinct_displays_use_oldest_surviving_operation_order_and_metadata()
    {
        var tracker = new CaptureActivityTracker();
        using var leftOld = tracker.CreateOperation();
        using var right = tracker.CreateOperation();
        using var leftNew = tracker.CreateOperation();
        var leftOldDisplay = Display("display-left", "Left at capture", scale: 1.25);
        var rightDisplay = Display("display-right", "Right at capture", scale: 1.5);
        var leftNewDisplay = Display("display-left", "Left after replacement", scale: 2);
        var expectedLeftOld = new DisplayInfo("display-left", "Left at capture", new RectD(0, 0, 1920, 1080), 1.25);
        var expectedRight = new DisplayInfo("display-right", "Right at capture", new RectD(0, 0, 1920, 1080), 1.5);
        var expectedLeftNew = new DisplayInfo("display-left", "Left after replacement", new RectD(0, 0, 1920, 1080), 2);

        leftOld.Start(leftOldDisplay);
        right.Start(rightDisplay);
        leftNew.Start(leftNewDisplay);

        Assert.Equal(new[] { expectedLeftOld, expectedRight }, tracker.ActiveDisplays);

        leftOld.Dispose();

        Assert.Equal(new[] { expectedRight, expectedLeftNew }, tracker.ActiveDisplays);
    }

    // CA-006
    [Fact]
    public void Older_completion_does_not_hide_a_newer_operation_on_the_same_display()
    {
        var tracker = new CaptureActivityTracker();
        using var older = tracker.CreateOperation();
        using var newer = tracker.CreateOperation();
        var olderMetadata = Display("display-1", "Older capture metadata");
        var newerMetadata = Display("display-1", "Newer capture metadata");
        var expectedNewerMetadata = new DisplayInfo("display-1", "Newer capture metadata", new RectD(0, 0, 1920, 1080), 1);

        older.Start(olderMetadata);
        newer.Start(newerMetadata);
        older.Dispose();

        Assert.Equal(new[] { expectedNewerMetadata }, tracker.ActiveDisplays);

        newer.Dispose();
        Assert.Empty(tracker.ActiveDisplays);
    }

    // CA-007
    [Fact]
    public void Previously_returned_snapshots_remain_stable_after_activity_changes()
    {
        var tracker = new CaptureActivityTracker();
        using var first = tracker.CreateOperation();
        using var second = tracker.CreateOperation();
        var firstDisplay = Display("display-1");
        var secondDisplay = Display("display-2");
        var expectedFirstDisplay = new DisplayInfo("display-1", "Fixture display", new RectD(0, 0, 1920, 1080), 1);
        var expectedSecondDisplay = new DisplayInfo("display-2", "Fixture display", new RectD(0, 0, 1920, 1080), 1);

        first.Start(firstDisplay);
        var snapshot = tracker.ActiveDisplays;

        second.Start(secondDisplay);
        first.Dispose();

        Assert.NotEmpty(snapshot);
        if (snapshot is IList<DisplayInfo> mutableSnapshot)
        {
            Assert.True(mutableSnapshot.IsReadOnly, "An exposed IList snapshot must be read-only.");
            Assert.Throws<NotSupportedException>(() => mutableSnapshot[0] = expectedSecondDisplay);
        }

        Assert.Equal(new[] { expectedFirstDisplay }, snapshot);
        Assert.Equal(new[] { expectedSecondDisplay }, tracker.ActiveDisplays);
    }

    // CA-008
    [Fact]
    public void Null_display_is_rejected_without_consuming_the_operation()
    {
        var tracker = new CaptureActivityTracker();
        using var operation = tracker.CreateOperation();

        Assert.Throws<ArgumentNullException>(() => operation.Start(null!));
        Assert.Empty(tracker.ActiveDisplays);

        operation.Start(Display("display-1"));
        Assert.Equal(
            new[] { new DisplayInfo("display-1", "Fixture display", new RectD(0, 0, 1920, 1080), 1) },
            tracker.ActiveDisplays);
    }

    // CA-009
    [Fact]
    public void Repeated_start_is_rejected_without_replacing_the_original_display()
    {
        var tracker = new CaptureActivityTracker();
        using var operation = tracker.CreateOperation();
        var original = Display("display-1", "Original display");
        var replacement = Display("display-2", "Replacement display");
        var expectedOriginal = new DisplayInfo("display-1", "Original display", new RectD(0, 0, 1920, 1080), 1);

        operation.Start(original);

        Assert.Throws<InvalidOperationException>(() => operation.Start(replacement));
        Assert.Equal(new[] { expectedOriginal }, tracker.ActiveDisplays);
    }

    // CA-010
    [Fact]
    public void Start_after_dispose_is_rejected()
    {
        var tracker = new CaptureActivityTracker();
        using var operation = tracker.CreateOperation();
        operation.Dispose();

        Assert.Throws<ObjectDisposedException>(() => operation.Start(Display("display-1")));
        Assert.Empty(tracker.ActiveDisplays);
    }

    // CA-011
    [Fact]
    public async Task Independent_operations_can_start_and_finish_concurrently()
    {
        const int operationCount = 8;
        var tracker = new CaptureActivityTracker();
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workersReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workersStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var startedCount = 0;
        var operations = Enumerable.Range(0, operationCount)
            .Select(_ => tracker.CreateOperation())
            .ToArray();
        var tasks = operations
            .Select((operation, index) => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == operationCount)
                {
                    workersReady.TrySetResult(true);
                }

                await startGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
                operation.Start(Display($"display-{index}", $"Display {index}"));

                if (Interlocked.Increment(ref startedCount) == operationCount)
                {
                    workersStarted.TrySetResult(true);
                }

                await releaseGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
                operation.Dispose();
            }))
            .ToArray();
        var allTasks = Task.WhenAll(tasks);

        try
        {
            await workersReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            startGate.TrySetResult(true);
            await workersStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var activeIds = tracker.ActiveDisplays.Select(display => display.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(operationCount, tracker.ActiveDisplays.Count);
            var expectedIds = new HashSet<string>(new[]
            {
                "display-0", "display-1", "display-2", "display-3",
                "display-4", "display-5", "display-6", "display-7"
            }, StringComparer.Ordinal);
            Assert.True(expectedIds.SetEquals(activeIds));

            releaseGate.TrySetResult(true);
            await allTasks.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(tracker.ActiveDisplays);
        }
        finally
        {
            startGate.TrySetResult(true);
            releaseGate.TrySetResult(true);
            try
            {
                await allTasks.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Preserve the assertion or watchdog failure above while still
                // allowing every operation to attempt cleanup.
            }

            foreach (var operation in operations)
            {
                operation.Dispose();
            }
        }
    }

    private static DisplayInfo Display(string id, string name = "Fixture display", double scale = 1)
    {
        return new DisplayInfo(id, name, new RectD(0, 0, 1920, 1080), scale);
    }
}
