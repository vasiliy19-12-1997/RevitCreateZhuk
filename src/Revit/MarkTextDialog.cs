using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk
{
    internal readonly struct MarkTextEdit
    {
        public MarkTextEdit(string text, Point2 offset)
        {
            Text = text;
            Offset = offset;
        }

        public string Text { get; }
        public Point2 Offset { get; }
    }

    internal sealed class MarkTextDialog : Form
    {
        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false
        };
        private readonly CheckBox native = new CheckBox
        {
            Text = "Создавать подписи разрезов и фасадов обычным текстом", AutoSize = true
        };

        public MarkTextDialog(IEnumerable<CadItem> labels, IReadOnlyDictionary<string, MarkTextEdit> edits, bool nativeText)
        {
            Text = "Подписи разрезов и фасадов";
            ClientSize = new Size(800, 430);
            MinimumSize = new Size(630, 320);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            Controls.Add(layout);
            layout.Controls.Add(new Label
            {
                Text = "Введите текст для схемы. Для переноса строки используйте \\n. Пустое поле возвращает текст DWG.\n" +
                    "Смещение в миллиметрах на листе. +X вправо, +Y вверх.",
                Dock = DockStyle.Fill
            }, 0, 0);
            layout.Controls.Add(grid, 0, 1);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Из DWG", ReadOnly = true, FillWeight = 29 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Текст на схеме", FillWeight = 53 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "X, мм", FillWeight = 9 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Y, мм", FillWeight = 9 });
            foreach (var label in labels.OrderBy(l => Composition.MarkTextRole(l)).ThenBy(l => l.Text))
            {
                edits.TryGetValue(label.Id, out var edit);
                int row = grid.Rows.Add(Composition.MarkName(label.Text),
                    (edit.Text ?? label.Text).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", @"\n"),
                    edit.Offset.X.ToString("0.###", CultureInfo.CurrentCulture),
                    edit.Offset.Y.ToString("0.###", CultureInfo.CurrentCulture));
                grid.Rows[row].Tag = label.Id;
            }

            native.Checked = nativeText;
            layout.Controls.Add(native, 0, 2);
            var hint = new Label { Dock = DockStyle.Fill };
            void UpdateHint() => hint.Text = native.Checked
                ? "Обычный текст можно выбрать, переместить и отредактировать прямо в семействе. Его видимость независима от обозначения."
                : "Связанная подпись скрывается вместе с обозначением. Её положение задаётся в этой таблице.";
            native.CheckedChanged += (_, __) => UpdateHint();
            UpdateHint();
            layout.Controls.Add(hint, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 90 };
            var ok = new Button { Text = "Готово", Width = 90 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons, 0, 4);
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += (_, __) => AcceptEdits();
        }

        public bool NativeText => native.Checked;
        public Dictionary<string, MarkTextEdit> Edits { get; private set; }

        private void AcceptEdits()
        {
            grid.EndEdit();
            var values = new Dictionary<string, MarkTextEdit>(StringComparer.Ordinal);
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!TryRead(row.Cells[2].Value, out double x) || !TryRead(row.Cells[3].Value, out double y))
                {
                    MessageBox.Show(this, "Введите смещение от −1000 до 1000 мм для подписи «" + row.Cells[0].Value + "».",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string value = Convert.ToString(row.Cells[1].Value)?.Replace(@"\n", "\n").Trim();
                values.Add((string)row.Tag, new MarkTextEdit(string.IsNullOrWhiteSpace(value) ? null : value, new Point2(x, y)));
            }
            Edits = values;
            DialogResult = DialogResult.OK;
        }

        private static bool TryRead(object value, out double result)
        {
            var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim();
            result = 0;
            if (string.IsNullOrEmpty(text)) return true;
            const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            return (double.TryParse(text, style, CultureInfo.CurrentCulture, out result) ||
                double.TryParse(text.Replace(',', '.'), style, CultureInfo.InvariantCulture, out result)) &&
                result >= -1000 && result <= 1000;
        }
    }
}
