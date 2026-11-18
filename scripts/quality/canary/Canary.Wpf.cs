// Banned-API canary, WPF part (docs/quality/RULES.md#canary): compiled only where UseWPF is true. Same marker rules
// as Canary.Core.cs: one call per line, ending in a BAN marker comment naming its doc id, everything fully qualified.
#pragma warning disable CA1416, CA1822, CS0618
namespace BooBannedApiCanary.Wpf;

internal static class Calls
{
    internal static void All(
        global::System.Windows.Threading.Dispatcher d,
        global::System.Action a,
        global::System.Func<int> f,
        global::System.Delegate del,
        object o,
        object[] items,
        global::System.Windows.Threading.DispatcherPriority p,
        global::System.Threading.CancellationToken ct,
        global::System.TimeSpan ts,
        global::System.Windows.Threading.DispatcherFrame frame)
    {
        d.Invoke(a); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Action)
        d.Invoke(a, p); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Action,System.Windows.Threading.DispatcherPriority)
        d.Invoke(a, p, ct); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Action,System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken)
        d.Invoke(a, p, ct, ts); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Action,System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken,System.TimeSpan)
        _ = d.Invoke(del, items); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.Object[])
        _ = d.Invoke(del, ts, items); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.TimeSpan,System.Object[])
        _ = d.Invoke(del, ts, p, items); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.TimeSpan,System.Windows.Threading.DispatcherPriority,System.Object[])
        _ = d.Invoke(del, p, items); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Delegate,System.Windows.Threading.DispatcherPriority,System.Object[])
        _ = d.Invoke(p, del); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.Delegate)
        _ = d.Invoke(p, del, o); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object)
        _ = d.Invoke(p, del, o, items); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object,System.Object[])
        _ = d.Invoke(p, ts, del); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.TimeSpan,System.Delegate)
        _ = d.Invoke(p, ts, del, o); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.TimeSpan,System.Delegate,System.Object)
        _ = d.Invoke(p, ts, del, o, items); // BAN M:System.Windows.Threading.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority,System.TimeSpan,System.Delegate,System.Object,System.Object[])
        _ = d.Invoke(f); // BAN M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0})
        _ = d.Invoke(f, p); // BAN M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0},System.Windows.Threading.DispatcherPriority)
        _ = d.Invoke(f, p, ct); // BAN M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0},System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken)
        _ = d.Invoke(f, p, ct, ts); // BAN M:System.Windows.Threading.Dispatcher.Invoke``1(System.Func{``0},System.Windows.Threading.DispatcherPriority,System.Threading.CancellationToken,System.TimeSpan)
        global::System.Windows.Threading.DispatcherExtensions.Invoke(d, a); // BAN M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action)
        global::System.Windows.Threading.DispatcherExtensions.Invoke(d, a, ts); // BAN M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action,System.TimeSpan)
        global::System.Windows.Threading.DispatcherExtensions.Invoke(d, a, ts, p); // BAN M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action,System.TimeSpan,System.Windows.Threading.DispatcherPriority)
        global::System.Windows.Threading.DispatcherExtensions.Invoke(d, a, p); // BAN M:System.Windows.Threading.DispatcherExtensions.Invoke(System.Windows.Threading.Dispatcher,System.Action,System.Windows.Threading.DispatcherPriority)
        _ = d.BeginInvoke(del, items); // BAN M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate,System.Object[])
        _ = d.BeginInvoke(del, p, items); // BAN M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate,System.Windows.Threading.DispatcherPriority,System.Object[])
        _ = d.BeginInvoke(p, del); // BAN M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority,System.Delegate)
        _ = d.BeginInvoke(p, del, o); // BAN M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object)
        _ = d.BeginInvoke(p, del, o, items); // BAN M:System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority,System.Delegate,System.Object,System.Object[])
        _ = global::System.Windows.Threading.DispatcherExtensions.BeginInvoke(d, a); // BAN M:System.Windows.Threading.DispatcherExtensions.BeginInvoke(System.Windows.Threading.Dispatcher,System.Action)
        _ = global::System.Windows.Threading.DispatcherExtensions.BeginInvoke(d, a, p); // BAN M:System.Windows.Threading.DispatcherExtensions.BeginInvoke(System.Windows.Threading.Dispatcher,System.Action,System.Windows.Threading.DispatcherPriority)
        global::System.Windows.Threading.Dispatcher.PushFrame(frame); // BAN M:System.Windows.Threading.Dispatcher.PushFrame(System.Windows.Threading.DispatcherFrame)
    }
}
