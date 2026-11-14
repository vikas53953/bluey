using System.Drawing;
using System.Windows.Forms;

namespace Boo.TypeTarget.App;

/// <summary>
/// Two fields in one window. "FieldA" is the approved field; "FieldB" takes keyboard focus as soon as the window is
/// shown. args[0] is the mode for A: present | missing | readonly | nonvalue, or a target-identity mode with an
/// editable decoy whose ID is not exactly "FieldA": casefirst (decoy "fielda" before a real "FieldA"), caseonly
/// (only "fielda"), partialonly (only "FieldA2"); or removeonwrite (A accepts the write, then is removed from the
/// window at once, so it cannot be read back).
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
            ClientSize = new Size(520, 200);
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

            Control? fieldA = mode switch
            {
                "present" or "casefirst" or "removeonwrite" => new TextBox { Name = "FieldA", AccessibleName = "Field A" },
                "readonly" => new TextBox { Name = "FieldA", AccessibleName = "Field A", ReadOnly = true },
                "nonvalue" => new Label { Name = "FieldA", AccessibleName = "Field A", Text = "Field A (not editable)" },
                "missing" or "caseonly" or "partialonly" => null,
                _ => throw new ArgumentException($"unknown mode '{mode}'"),
            };
            if (fieldA is not null)
            {
                fieldA.SetBounds(20, 20, 480, 30);
                Controls.Add(fieldA);
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
}
