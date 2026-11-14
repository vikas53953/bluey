using Boo.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Boo.Windows;

/// <summary>
/// Executes the small set of typed desktop tools.  There is intentionally no
/// shell escape hatch here: model strings are data, not commands.
/// </summary>
public sealed class DesktopTools
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint GetAncestorRoot = 2;
    private const int MaximumTypedCharacters = 1024;

    private readonly RequestCoordinator _coordinator;
    private readonly TargetObserver _targets;

    // This is a closed registry of executable identities.  In particular,
    // an arbitrary model-provided path or command line is never accepted.
    private static readonly IReadOnlyDictionary<string, string> RegisteredApplications =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            ["explorer"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            ["paint"] = Path.Combine(Environment.SystemDirectory, "mspaint.exe")
        };

    public DesktopTools(RequestCoordinator coordinator, TargetObserver targets)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public Task<ToolOutcome> ExecuteAsync(
        ToolRequest request,
        TaskAuthority authority,
        ApprovalTicket? approval,
        WindowIdentity? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);

        try
        {
            var policy = ActionPolicy.Evaluate(request, authority, approval);
            if (policy.Verdict != PermissionVerdict.Allowed)
            {
                return Task.FromResult(new ToolOutcome(false, false, policy.Reason));
            }

            if (cancellationToken.IsCancellationRequested || !_coordinator.IsCurrent(request.Generation))
            {
                return Task.FromResult(Cancelled());
            }

            // Second line behind the approval card: never drive a shell, an admin
            // console or Boo itself, and never type hidden control characters.
            if (WindowActionGuard.BlockedReason(request, target, Environment.ProcessId) is { } windowBlock)
            {
                return Task.FromResult(new ToolOutcome(false, false, windowBlock));
            }

            var outcome = request.Name switch
            {
                "search" => Search(),
                "inspect" => Inspect(target),
                "open_url" => OpenUrl(request, authority, approval, cancellationToken),
                "open_app" => OpenApp(request, authority, approval, cancellationToken),
                "click" => Click(request, authority, approval, target, cancellationToken),
                "type_text" => TypeText(request, authority, approval, target, cancellationToken),
                "write_file" => WriteFile(request, authority, approval, cancellationToken),
                "delete_file" => DeleteFile(request, authority, approval, cancellationToken),
                _ => new ToolOutcome(false, false, "The requested tool is not supported.")
            };

            return Task.FromResult(outcome);
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Cancelled());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(new ToolOutcome(false, false, $"unverified: the desktop action could not be completed ({exception.GetType().Name})."));
        }
    }

    private ToolOutcome Search() =>
        new(false, false, "unverified: search must be performed by the provider; no shell or local command was run.");

    private ToolOutcome Inspect(WindowIdentity? target)
    {
        if (target is null)
        {
            return new ToolOutcome(false, false, "unverified: no independently identified target window was supplied.");
        }

        if (!TryVerifyTarget(target, out var reason))
        {
            return new ToolOutcome(false, false, $"unverified: target inspection was refused ({reason}).");
        }

        return new ToolOutcome(
            true,
            false,
            $"observed target hwnd={target.Handle}, pid={target.ProcessId}, process={target.ProcessName}, bounds={FormatBounds(target.Bounds)}.");
    }

    private ToolOutcome OpenUrl(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval, CancellationToken cancellationToken)
    {
        if (!IsSafeHttpUrl(request.Target))
        {
            return new ToolOutcome(false, false, "The URL is not an absolute HTTP(S) URL with a host.");
        }

        var launched = false;
        var committed = _coordinator.TryCommit(request.Generation, () =>
        {
            if (cancellationToken.IsCancellationRequested || !IsStillAuthorized(request, authority, approval))
            {
                return;
            }

            // Hand the browser the parsed, normalized URL rather than the raw model string.
            Process.Start(new ProcessStartInfo
            {
                FileName = new Uri(request.Target, UriKind.Absolute).AbsoluteUri,
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory
            });
            launched = true;
        });

        if (!committed)
        {
            return Cancelled();
        }

        return launched
            ? new ToolOutcome(false, true, "partial/unverified: the approved HTTP(S) URL was handed to the default browser; browser navigation was not read back.")
            : Cancelled();
    }

    private ToolOutcome OpenApp(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval, CancellationToken cancellationToken)
    {
        if (!RegisteredApplications.TryGetValue(request.Target.Trim(), out var executable) || !File.Exists(executable))
        {
            return new ToolOutcome(false, false, "The app ID is not in Boo's registered executable map.");
        }

        Process? process = null;
        var committed = _coordinator.TryCommit(request.Generation, () =>
        {
            if (cancellationToken.IsCancellationRequested || !IsStillAuthorized(request, authority, approval))
            {
                return;
            }

            process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        });

        if (!committed)
        {
            return Cancelled();
        }

        if (process is null)
        {
            return new ToolOutcome(false, true, "partial/unverified: the registered app launch returned no process handle.");
        }

        try
        {
            if (!process.HasExited)
            {
                return new ToolOutcome(true, true, $"observed registered app process {process.Id} running.");
            }
        }
        finally
        {
            process.Dispose();
        }

        return new ToolOutcome(false, true, "partial/unverified: the registered app exited before Boo could observe it running.");
    }

    private ToolOutcome Click(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval, WindowIdentity? target, CancellationToken cancellationToken)
    {
        if (!TryVerifyTarget(target, out var reason))
        {
            return new ToolOutcome(false, false, $"unverified: click refused because the target is not current ({reason}).");
        }

        var targetWindow = target!;
        var pointAvailable = TryResolvePoint(request, targetWindow, out var point, out reason);
        var hasObservedElementRequest = request.Arguments.TryGetValue("automationId", out var automationId) && !string.IsNullOrWhiteSpace(automationId);
        if (!pointAvailable && !hasObservedElementRequest)
        {
            return new ToolOutcome(false, false, $"unverified: click needs explicit coordinates or an observed UI Automation element ({reason}).");
        }

        var uiAutomationUsed = false;
        var inputSent = false;
        var sendInputPrepared = false;
        var committed = _coordinator.TryCommit(request.Generation, () =>
        {
            if (cancellationToken.IsCancellationRequested || !IsStillAuthorized(request, authority, approval) || !TryVerifyTarget(targetWindow, out _))
            {
                return;
            }

            if (TryInvokeAccessibleTarget(targetWindow, request.Arguments, out _))
            {
                uiAutomationUsed = true;
                return;
            }

            if (!pointAvailable && !TryResolveAccessibleCenter(targetWindow, request.Arguments, out point))
            {
                return;
            }

            if (!TryPrepareSendInputTarget(targetWindow, point, out _))
            {
                return;
            }

            sendInputPrepared = true;
            inputSent = SendMouseClick(point);
        });

        if (!committed)
        {
            return Cancelled();
        }

        if (uiAutomationUsed)
        {
            return new ToolOutcome(false, true, "partial/unverified: UI Automation invoked the approved target, but no post-action state was available to verify.");
        }

        return inputSent
            ? new ToolOutcome(false, true, "partial/unverified: bounded SendInput click was issued, but no post-action state was available to verify.")
            : sendInputPrepared
                ? new ToolOutcome(false, true, "partial/unverified: the click input was not accepted by Windows.")
                : new ToolOutcome(false, true, "partial/unverified: SendInput was refused because foreground, focus, process, or hit-window verification failed.");
    }

    private ToolOutcome TypeText(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval, WindowIdentity? target, CancellationToken cancellationToken)
    {
        if (!TryVerifyTarget(target, out var reason))
        {
            return new ToolOutcome(false, false, $"unverified: typing refused because the target is not current ({reason}).");
        }

        if (!request.Arguments.TryGetValue("text", out var text) || text is null || text.Length == 0)
        {
            return new ToolOutcome(false, false, "No text was supplied for the approved type_text action.");
        }

        if (text.Length > MaximumTypedCharacters)
        {
            return new ToolOutcome(false, false, $"Text exceeds Boo's bounded {MaximumTypedCharacters}-character input limit.");
        }

        var namedField = NamedField(request.Arguments);
        if (namedField is null)
        {
            return new ToolOutcome(false, false, "Can't type safely: this field has no stable identity (no automation ID was approved). Boo only types into a named field it can set and read back. Nothing was typed.");
        }

        var targetWindow = target!;
        var result = FieldWrite.NotAttempted;
        var valueCommit = _coordinator.TryCommit(request.Generation, () =>
        {
            if (cancellationToken.IsCancellationRequested || !IsStillAuthorized(request, authority, approval) || !TryVerifyTarget(targetWindow, out _))
            {
                return;
            }

            result = WriteNamedField(targetWindow, namedField, text);
        });

        return result switch
        {
            FieldWrite.ReadBackMatched when valueCommit =>
                new ToolOutcome(true, true, "observed UI Automation value read back exactly after typing."),
            FieldWrite.ReadBackMismatched =>
                new ToolOutcome(false, true, "partial/unverified: UI Automation set the value, but the target text readback did not match."),
            FieldWrite.ReadBackUnavailable =>
                new ToolOutcome(false, true, $"partial/unverified: the value was written to the approved field '{namedField}', but it could not be read back (the field changed or disappeared). No other field was typed into, and Boo will not retry."),
            FieldWrite.WriteUnconfirmed =>
                new ToolOutcome(false, true, $"partial/unverified: Windows reported an error while writing to the approved field '{namedField}', so it may have changed and could not be read back. No other field was typed into, and Boo will not retry."),
            FieldWrite.NotFound =>
                new ToolOutcome(false, false, $"Typing refused: the approved field '{namedField}' is no longer in the target window. Nothing was typed."),
            FieldWrite.NotSettable =>
                new ToolOutcome(false, false, $"Typing refused: the approved field '{namedField}' cannot be set safely (read-only or not a text field). Nothing was typed."),
            _ when !valueCommit => Cancelled(),
            _ => new ToolOutcome(false, false, $"Typing refused: the approved field '{namedField}' could not be confirmed. Nothing was typed."),
        };
    }

    private ToolOutcome WriteFile(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval, CancellationToken cancellationToken)
    {
        FileActionPlan plan;
        try
        {
            plan = FileActionResolver.Resolve(request, authority);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ToolOutcome(false, false, exception.Message);
        }

        var canonical = plan.CanonicalPath;
        var parent = Path.GetDirectoryName(canonical)!;
        var bytes = System.Text.Encoding.UTF8.GetBytes(plan.Contents!);
        var committed = false;
        var mutationCommitted = false;
        var success = false;
        var message = "partial/unverified: file write did not reach its commit boundary.";
        committed = _coordinator.TryCommit(request.Generation, () =>
        {
            if (cancellationToken.IsCancellationRequested || !IsStillAuthorized(request, authority, approval))
            {
                message = "cancelled before file commit.";
                return;
            }

            FileActionPlan freshPlan;
            try
            {
                freshPlan = FileActionResolver.Resolve(request, authority);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                message = $"partial/unverified: the file path changed before commit ({exception.GetType().Name}).";
                return;
            }

            if (!PathEquals(canonical, freshPlan.CanonicalPath) ||
                !string.Equals(plan.Contents, freshPlan.Contents, StringComparison.Ordinal) ||
                plan.Overwrite != freshPlan.Overwrite ||
                !Directory.Exists(parent))
            {
                message = "partial/unverified: the approved file action changed before commit.";
                return;
            }

            var stagingPath = Path.Combine(parent, $".{Path.GetFileName(canonical)}.boo-stage-{Guid.NewGuid():N}");
            try
            {
                using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    message = "cancelled before staged file replacement.";
                    return;
                }

                File.Move(stagingPath, canonical, plan.Overwrite);
                mutationCommitted = true;
                var readBack = File.ReadAllBytes(canonical);
                success = readBack.AsSpan().SequenceEqual(bytes);
                message = success
                    ? $"observed file readback matches the approved UTF-8 content at {canonical}."
                    : "partial/unverified: file replacement completed but readback did not match the approved bytes.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                message = $"partial/unverified: file commit failed ({exception.GetType().Name}).";
            }
            finally
            {
                TryDelete(stagingPath);
            }
        });

        if (!committed)
        {
            return Cancelled();
        }

        return new ToolOutcome(success, mutationCommitted, message);
    }

    private ToolOutcome DeleteFile(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval, CancellationToken cancellationToken)
    {
        FileActionPlan plan;
        try
        {
            plan = FileActionResolver.Resolve(request, authority);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ToolOutcome(false, false, exception.Message);
        }

        var canonical = plan.CanonicalPath;
        var deleted = false;
        var committed = _coordinator.TryCommit(request.Generation, () =>
        {
            if (cancellationToken.IsCancellationRequested || !IsStillAuthorized(request, authority, approval))
            {
                return;
            }

            FileActionPlan freshPlan;
            try
            {
                freshPlan = FileActionResolver.Resolve(request, authority);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return;
            }

            if (!PathEquals(canonical, freshPlan.CanonicalPath))
            {
                return;
            }

            File.Delete(canonical);
            deleted = !File.Exists(canonical);
        });

        if (!committed)
        {
            return Cancelled();
        }

        return deleted
            ? new ToolOutcome(true, true, $"observed file deletion at {canonical}.")
            : new ToolOutcome(false, true, "partial/unverified: delete commit ran but the target was not observed absent.");
    }

    private bool TryVerifyTarget(WindowIdentity? target, out string reason)
    {
        reason = "missing target identity";
        if (target is null || target.Handle <= 0 || target.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(target.ProcessName) ||
            !double.IsFinite(target.Bounds.X) || !double.IsFinite(target.Bounds.Y) ||
            !double.IsFinite(target.Bounds.Width) || !double.IsFinite(target.Bounds.Height) ||
            target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
        {
            return false;
        }

        var handle = new nint(target.Handle);
        if (!IsWindow(handle))
        {
            reason = "the HWND is no longer a window";
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var actualProcessId);
        if (actualProcessId != target.ProcessId)
        {
            reason = "the HWND belongs to a different process";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (!process.ProcessName.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                reason = "the process identity changed";
                return false;
            }
        }
        catch
        {
            reason = "the target process is unavailable";
            return false;
        }

        if (!GetWindowRect(handle, out var actualBounds) ||
            !ApproximatelyEqual(target.Bounds, actualBounds))
        {
            reason = "the target bounds changed";
            return false;
        }

        if (!TryGetIntegrityLevel(Process.GetCurrentProcess().Handle, out var ownIntegrity) ||
            !TryGetProcessIntegrityLevel(target.ProcessId, out var targetIntegrity))
        {
            reason = "target integrity could not be verified";
            return false;
        }

        if (targetIntegrity > ownIntegrity)
        {
            reason = "the target has higher integrity than Boo and cannot be controlled safely";
            return false;
        }

        try
        {
            if (!_targets.IsCurrent(target))
            {
                reason = "the target is no longer the independently observed current target";
                return false;
            }
        }
        catch
        {
            reason = "target observation is unavailable";
            return false;
        }

        return true;
    }

    private static bool ApproximatelyEqual(RectD expected, RECT actual) =>
        Math.Abs(expected.X - actual.Left) <= 2 &&
        Math.Abs(expected.Y - actual.Top) <= 2 &&
        Math.Abs(expected.Width - (actual.Right - actual.Left)) <= 2 &&
        Math.Abs(expected.Height - (actual.Bottom - actual.Top)) <= 2;

    private static string FormatBounds(RectD bounds) =>
        $"({bounds.X:0},{bounds.Y:0},{bounds.Width:0},{bounds.Height:0})";

    private static bool TryResolvePoint(ToolRequest request, WindowIdentity target, out POINT point, out string reason)
    {
        point = default;
        reason = string.Empty;
        if (!request.Arguments.TryGetValue("x", out var xValue) ||
            !request.Arguments.TryGetValue("y", out var yValue))
        {
            reason = "no explicit coordinates were supplied";
            return false;
        }

        if (!double.TryParse(xValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
        {
            reason = "x is not numeric";
            return false;
        }

        if (!double.TryParse(yValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
        {
            reason = "y is not numeric";
            return false;
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            reason = "coordinates are not finite";
            return false;
        }

        if (request.Arguments.ContainsKey("x") && request.Arguments.ContainsKey("y") &&
            x >= 0 && x <= target.Bounds.Width && y >= 0 && y <= target.Bounds.Height)
        {
            x += target.Bounds.X;
            y += target.Bounds.Y;
        }

        point = new POINT((int)Math.Round(x), (int)Math.Round(y));
        if (point.X < target.Bounds.X || point.X > target.Bounds.X + target.Bounds.Width ||
            point.Y < target.Bounds.Y || point.Y > target.Bounds.Y + target.Bounds.Height)
        {
            reason = "coordinates fall outside the independently observed target bounds";
            return false;
        }

        return true;
    }

    private static bool TryResolveAccessibleCenter(WindowIdentity target, IReadOnlyDictionary<string, string> arguments, out POINT point)
    {
        point = default;
        if (!arguments.TryGetValue("automationId", out var automationId) || string.IsNullOrWhiteSpace(automationId))
        {
            return false;
        }

        try
        {
            var root = AutomationElement.FromHandle(new nint(target.Handle));
            var element = FindElement(root, arguments);
            if (element is null)
            {
                return false;
            }

            var bounds = element.Current.BoundingRectangle;
            var x = bounds.Left + bounds.Width / 2;
            var y = bounds.Top + bounds.Height / 2;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(x) || !double.IsFinite(y))
            {
                return false;
            }

            point = new POINT((int)Math.Round(x), (int)Math.Round(y));
            return point.X >= target.Bounds.X && point.X <= target.Bounds.X + target.Bounds.Width &&
                point.Y >= target.Bounds.Y && point.Y <= target.Bounds.Y + target.Bounds.Height;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool TryInvokeAccessibleTarget(WindowIdentity target, IReadOnlyDictionary<string, string> arguments, out string reason)
    {
        reason = string.Empty;
        try
        {
            var root = AutomationElement.FromHandle(new nint(target.Handle));
            var element = FindElement(root, arguments);
            if (element is null || !element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                return false;
            }

            ((InvokePattern)pattern).Invoke();
            reason = "UI Automation InvokePattern completed";
            return true;
        }
        catch (ElementNotAvailableException exception)
        {
            reason = exception.Message;
            return false;
        }
        catch (InvalidOperationException exception)
        {
            reason = exception.Message;
            return false;
        }
        catch (COMException exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// What happened to the field a type_text request named. Three separate steps: find and check the field (a failure
    /// here is a refusal, nothing written); the write (once <see cref="ValuePattern.SetValue"/> is called, every result
    /// is committed: an error from the write itself may still have changed the field, so it is
    /// <see cref="WriteUnconfirmed"/>, never "nothing was typed"); the readback, which looks the field up again by its
    /// exact ID, so a field removed or replaced right after the write is unreadable rather than read from a detached
    /// control. Nothing is ever retried: a retry would type the text twice.
    /// </summary>
    private enum FieldWrite
    {
        NotAttempted,
        NotFound,
        NotSettable,
        ReadBackMatched,
        ReadBackMismatched,
        ReadBackUnavailable,
        WriteUnconfirmed,
    }

    private static string? NamedField(IReadOnlyDictionary<string, string> arguments) =>
        arguments.TryGetValue("automationId", out var automationId) && !string.IsNullOrWhiteSpace(automationId)
            ? automationId
            : null;

    private static AutomationElement? FindExactField(WindowIdentity target, string automationId) =>
        AutomationElement.FromHandle(new nint(target.Handle)).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static FieldWrite WriteNamedField(WindowIdentity target, string automationId, string value)
    {
        ValuePattern pattern;
        try
        {
            var element = FindExactField(target, automationId);
            if (element is null)
            {
                return FieldWrite.NotFound;
            }

            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var found) || ((ValuePattern)found).Current.IsReadOnly)
            {
                return FieldWrite.NotSettable;
            }

            pattern = (ValuePattern)found;
        }
        catch (ElementNotAvailableException)
        {
            return FieldWrite.NotFound;
        }
        catch (InvalidOperationException)
        {
            return FieldWrite.NotSettable;
        }
        catch (COMException)
        {
            return FieldWrite.NotSettable;
        }

        if (!TrySetOnce(pattern, value))
        {
            return FieldWrite.WriteUnconfirmed;
        }

        return ReadBackExactField(target, automationId) is not { } readBack
            ? FieldWrite.ReadBackUnavailable
            : string.Equals(readBack, value, StringComparison.Ordinal)
                ? FieldWrite.ReadBackMatched
                : FieldWrite.ReadBackMismatched;
    }

    /// <summary>
    /// The single write attempt. False only when the write call itself failed, after which the field may or may not
    /// have changed; the caller reports that as committed and unverified and never calls this again.
    /// </summary>
    private static bool TrySetOnce(ValuePattern pattern, string value)
    {
        try
        {
            pattern.SetValue(value);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// The approved field's value, read through a fresh exact-ID lookup after the write, or null when it cannot be
    /// read. It returns only a value or null, never a <see cref="FieldWrite"/>, so a readback failure can never be
    /// reported as "not written" whichever error caused it.
    /// </summary>
    private static string? ReadBackExactField(WindowIdentity target, string automationId)
    {
        try
        {
            var element = FindExactField(target, automationId);
            return element is not null && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                ? ((ValuePattern)pattern).Current.Value
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static AutomationElement? FindElement(AutomationElement root, IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("automationId", out var automationId) || string.IsNullOrWhiteSpace(automationId))
        {
            return root;
        }

        return root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
    }

    private static bool SendMouseClick(POINT point)
    {
        if (!SetCursorPos(point.X, point.Y))
        {
            return false;
        }

        var inputs = new[]
        {
            new INPUT { Type = InputMouse, Data = new INPUTUNION { MouseInput = new MOUSEINPUT { Flags = MouseEventLeftDown } } },
            new INPUT { Type = InputMouse, Data = new INPUTUNION { MouseInput = new MOUSEINPUT { Flags = MouseEventLeftUp } } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private bool TryPrepareSendInputTarget(WindowIdentity target, POINT? point, out string reason)
    {
        if (!TryVerifyTarget(target, out reason))
        {
            return false;
        }

        var targetHandle = new nint(target.Handle);
        var foreground = GetForegroundWindow();
        if (!IsWindowWithinTarget(foreground, target))
        {
            if (!SetForegroundWindow(targetHandle))
            {
                reason = "Windows refused safe foreground activation of the approved target";
                return false;
            }

            foreground = GetForegroundWindow();
        }

        if (!IsWindowWithinTarget(foreground, target))
        {
            reason = "the foreground HWND is not the approved target or one of its child controls";
            return false;
        }

        if (!TryGetFocusedWindow(foreground, out var focused) || !IsWindowWithinTarget(focused, target))
        {
            reason = "the focused HWND is not the approved target or one of its child controls";
            return false;
        }

        if (point is { } screenPoint)
        {
            var hit = WindowFromPoint(screenPoint);
            if (!IsWindowWithinTarget(hit, target))
            {
                reason = "the actual hit HWND is not the approved target or one of its child controls";
                return false;
            }
        }

        // Activation can change the foreground target or window geometry. Do
        // one final independent identity check immediately before SendInput.
        return TryVerifyTarget(target, out reason);
    }

    private static bool IsWindowWithinTarget(nint window, WindowIdentity target)
    {
        if (window == 0 || !IsWindow(window))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId != target.ProcessId)
        {
            return false;
        }

        var targetRoot = GetAncestor(new nint(target.Handle), GetAncestorRoot);
        var windowRoot = GetAncestor(window, GetAncestorRoot);
        return targetRoot != 0 && targetRoot == windowRoot;
    }

    private static bool TryGetFocusedWindow(nint foreground, out nint focused)
    {
        focused = 0;
        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread == 0)
        {
            return false;
        }

        var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info))
        {
            return false;
        }

        focused = info.Focus;
        return focused != 0;
    }

    private static bool TryGetProcessIntegrityLevel(int processId, out int integrity)
    {
        integrity = 0;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return TryGetIntegrityLevel(process, out integrity);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool TryGetIntegrityLevel(IntPtr process, out int integrity)
    {
        integrity = 0;
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                {
                    return false;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var countPointer = GetSidSubAuthorityCount(sid);
                var count = Marshal.ReadByte(countPointer);
                var authority = GetSidSubAuthority(sid, (uint)(count - 1));
                integrity = Marshal.ReadInt32(authority);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeHttpUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // "https://bank.example@evil.test" shows a trusted name on the approval
        // card but opens a different host, so user-info URLs are never offered.
        return (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Staging cleanup is best effort; it is never reported as a
            // successful user action.
        }
    }

    private static ToolOutcome Cancelled() =>
        new(false, false, "stopped/cancelled: the request generation is no longer permitted to cause effects.");

    private static bool IsStillAuthorized(ToolRequest request, TaskAuthority authority, ApprovalTicket? approval)
    {
        return ActionPolicy.Evaluate(request, authority, approval).Verdict == PermissionVerdict.Allowed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public RECT CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT MouseInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint handle, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetAncestor(nint handle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

