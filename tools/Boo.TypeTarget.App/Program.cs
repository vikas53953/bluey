using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

namespace Boo.TypeTarget.App;

/// <summary>
/// Two fields in one window. "FieldA" is the approved field; "FieldB" takes keyboard focus as soon as the window is
/// shown. args[0] is the mode for A: present | missing | readonly | nonvalue, or a target-identity mode with an
/// editable decoy whose ID is not exactly "FieldA": casefirst (decoy "fielda" before a real "FieldA"), caseonly
/// (only "fielda"), partialonly (only "FieldA2"); or removeonwrite (A accepts the write, then is removed from the
/// window at once, so it cannot be read back); or destroyonwrite (A is destroyed while it handles the write).
/// The provider-* modes make A a field with its own UI Automation provider, so the test controls exactly what
/// happens: provider-ok (write and readback work); after the write is stored, reading the value fails with "element
/// not available" (provider-readgone), "invalid operation" (provider-readinvalid) or a plain COM error
/// (provider-readfails, which UI Automation hands back as an empty value); provider-setthrows (the write changes the
/// value, then the write call fails with a COM error). In those modes the read-only "WriteCount" field shows how many
/// times A's value was written.
/// </summary>
internal static class Program
{
    public const string Title = "Boo type target";

    [STAThread]
    private static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "present";
        ApplicationConfiguration.Initialize();
        Application.Run(new TargetForm(mode));
    }

    private sealed class TargetForm : Form
    {
        private readonly TextBox _fieldB;

        public TargetForm(string mode)
        {
            Text = $"{Title} ({mode})";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(200, 200);
            ClientSize = new Size(520, 240);
            TopMost = true;

            Control? decoy = mode switch
            {
                "casefirst" or "caseonly" => new TextBox { Name = "fielda", AccessibleName = "Decoy" },
                "partialonly" => new TextBox { Name = "FieldA2", AccessibleName = "Decoy" },
                _ => null,
            };
            if (decoy is not null)
            {
                decoy.SetBounds(20, 55, 480, 30);
                Controls.Add(decoy);
            }

            TextBox? writeCount = null;
            if (mode.StartsWith("provider-", StringComparison.Ordinal))
            {
                writeCount = new TextBox { Name = "WriteCount", AccessibleName = "Write count", ReadOnly = true, Text = "0" };
                writeCount.SetBounds(20, 170, 480, 30);
                Controls.Add(writeCount);
            }

            Control? fieldA = mode switch
            {
                "present" or "casefirst" or "removeonwrite" or "destroyonwrite" => new TextBox { Name = "FieldA", AccessibleName = "Field A" },
                "readonly" => new TextBox { Name = "FieldA", AccessibleName = "Field A", ReadOnly = true },
                "nonvalue" => new Label { Name = "FieldA", AccessibleName = "Field A", Text = "Field A (not editable)" },
                "provider-ok" or "provider-readgone" or "provider-readinvalid" or "provider-readfails" or "provider-setthrows" => new ProviderField(mode, writeCount!),
                "missing" or "caseonly" or "partialonly" => null,
                _ => throw new ArgumentException($"unknown mode '{mode}'"),
            };
            if (fieldA is not null)
            {
                fieldA.SetBounds(20, 20, 480, 30);
                Controls.Add(fieldA);
                if (mode == "destroyonwrite")
                {
                    fieldA.TextChanged += (_, _) =>
                    {
                        if (fieldA.Text.Length > 0 && !fieldA.IsDisposed)
                        {
                            Controls.Remove(fieldA);
                            fieldA.Dispose();
                        }
                    };
                }
                if (mode == "removeonwrite")
                {
                    fieldA.TextChanged += (_, _) =>
                    {
                        if (fieldA.Text.Length > 0)
                        {
                            Controls.Remove(fieldA);
                        }
                    };
                }
            }

            _fieldB = new TextBox { Name = "FieldB", AccessibleName = "Field B" };
            _fieldB.SetBounds(20, 120, 480, 30);
            Controls.Add(_fieldB);

            Shown += (_, _) =>
            {
                Activate();
                _fieldB.Focus();
            };
        }
    }

    /// <summary>
    /// "FieldA" with its own UI Automation provider (answering WM_GETOBJECT), so a test can make the value readback or
    /// the write itself fail on purpose, and count every write. Calls arrive on the UI thread (COM threading).
    /// </summary>
    private sealed class ProviderField : Control, IRawElementProviderSimple, IValueProvider
    {
        private const int WmGetObject = 0x003D;
        private const int EFail = unchecked((int)0x80004005);
        private const int UiaElementNotAvailable = unchecked((int)0x80040201);
        private const int UiaInvalidOperation = unchecked((int)0x80131509);
        private readonly string _mode;
        private readonly TextBox _writeCount;
        private IntPtr _hwnd;
        private string _value = "";
        private int _writes;

        public ProviderField(string mode, TextBox writeCount)
        {
            _mode = mode;
            _writeCount = writeCount;
            Name = "FieldA";
            BackColor = Color.White;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            _hwnd = Handle;
            base.OnHandleCreated(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmGetObject && m.LParam.ToInt64() == AutomationInteropProvider.RootObjectId)
            {
                m.Result = AutomationInteropProvider.ReturnRawElementProvider(_hwnd, m.WParam, m.LParam, this);
                return;
            }
            base.WndProc(ref m);
        }

        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

        public IRawElementProviderSimple HostRawElementProvider => AutomationInteropProvider.HostProviderFromHandle(_hwnd);

        public object GetPatternProvider(int patternId) => patternId == ValuePatternIdentifiers.Pattern.Id ? this : null!;

        public object GetPropertyValue(int propertyId)
        {
            if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id) return "FieldA";
            if (propertyId == AutomationElementIdentifiers.NameProperty.Id) return "Field A";
            if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.Edit.Id;
            if (propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id) return true;
            return null!;
        }

        public bool IsReadOnly => false;

        public string Value
        {
            get
            {
                var readError = _mode switch
                {
                    "provider-readgone" => UiaElementNotAvailable,
                    "provider-readinvalid" => UiaInvalidOperation,
                    "provider-readfails" => EFail,
                    _ => 0,
                };
                if (readError != 0 && _writes > 0)
                {
                    throw new COMException("FieldA cannot be read after the write (test target)", readError);
                }
                return _value;
            }
        }

        public void SetValue(string value)
        {
            _writes++;
            _writeCount.Text = _writes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _value = value;
            if (_mode == "provider-setthrows")
            {
                throw new COMException("FieldA changed, then the write failed (test target)", EFail);
            }
        }
    }
}
