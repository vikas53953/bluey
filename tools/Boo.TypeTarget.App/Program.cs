using System.Drawing;
using System.Windows.Forms;

namespace Boo.TypeTarget.App;

/// <summary>
/// Two fields in one window. "FieldA" is the approved field; "FieldB" takes keyboard focus as soon as the window is
/// shown. args[0] is the mode for A: present | missing | readonly | nonvalue.
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
            ClientSize = new Size(520, 180);
            TopMost = true;

            Control? fieldA = mode switch
            {
                "present" => new TextBox { Name = "FieldA", AccessibleName = "Field A" },
                "readonly" => new TextBox { Name = "FieldA", AccessibleName = "Field A", ReadOnly = true },
                "nonvalue" => new Label { Name = "FieldA", AccessibleName = "Field A", Text = "Field A (not editable)" },
                "missing" => null,
                _ => throw new ArgumentException($"unknown mode '{mode}'"),
            };
            if (fieldA is not null)
            {
                fieldA.SetBounds(20, 20, 480, 30);
                Controls.Add(fieldA);
            }

            _fieldB = new TextBox { Name = "FieldB", AccessibleName = "Field B" };
            _fieldB.SetBounds(20, 90, 480, 30);
            Controls.Add(_fieldB);

            Shown += (_, _) =>
            {
                Activate();
                _fieldB.Focus();
            };
        }
    }
}
