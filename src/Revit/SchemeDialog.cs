using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RevitCreateZhuk.Core;
using CadDrawing = RevitCreateZhuk.Core.Drawing;

namespace RevitCreateZhuk
{
    internal sealed class SchemeDialog : Form
    {
        private readonly TextBox source = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly CheckedListBox parkingLevels = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true,
            IntegralHeight = false };
        private readonly Label parkingStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        private readonly TextBox template = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly ComboBox schemes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly NumericUpDown scale = new NumericUpDown { Minimum = 1, Maximum = 100000, Value = 1000, Width = 84 };
        private readonly NumericUpDown units = new NumericUpDown { Minimum = .000001m, Maximum = 10000000, Value = 1, DecimalPlaces = 6, Width = 113 };
        private readonly CheckBox hatch = new CheckBox { Text = "Штриховка", AutoSize = true, Checked = true };
        private readonly TextBox summary = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = true,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly Panel preview = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 31, 39) };
        private readonly CheckBox useZone = new CheckBox { Text = "Включить схему", AutoSize = true };
        private readonly CheckBox overlay = new CheckBox { Text = "Совместить связанные схемы", AutoSize = true, Checked = true };
        private readonly HashSet<Zone> selected = new HashSet<Zone>();
        private Dictionary<string, MarkTextEdit> markEdits = new Dictionary<string, MarkTextEdit>(StringComparer.Ordinal);
        private readonly Dictionary<Zone, List<Building>> choices = new Dictionary<Zone, List<Building>>();
        private List<Zone> zones = new List<Zone>();
        private readonly List<Zone> parkingZones = new List<Zone>();
        private CadDrawing drawing;
        private CadDrawing parkingDrawing;
        private string parkingSource;
        private Zone zone;
        private bool changing;

        public SchemeDialog(string defaultTemplate, string target)
        {
            Text = "RevitCreateZhuk · схема из DWG";
            ClientSize = new Size(1020, 680);
            MinimumSize = new Size(860, 600);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            template.Text = defaultTemplate ?? "";
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            foreach (int height in new[] { 32, 32, 32, 32, 38, 0, 68, 42 })
                layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
            Controls.Add(layout);

            var targetLabel = new Label { Text = target, AutoSize = true, Dock = DockStyle.Fill };
            layout.Controls.Add(targetLabel, 0, 0);
            layout.SetColumnSpan(targetLabel, 3);
            AddRow(1, "Исходный DWG", source, "Выбрать DWG…", PickDrawing);
            AddRow(2, "Шаблон аннотации", template, "Выбрать RFT…", PickTemplate);
            layout.Controls.Add(new Label { Text = "Схема в файле", AutoSize = true }, 0, 3);
            layout.Controls.Add(schemes, 1, 3);
            layout.Controls.Add(useZone, 2, 3);

            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            settings.Controls.Add(new Label { Text = "Масштаб 1:", AutoSize = true, Margin = new Padding(0, 7, 3, 0) });
            settings.Controls.Add(scale);
            settings.Controls.Add(new Label { Text = "Мм в единице DWG", AutoSize = true, Margin = new Padding(14, 7, 3, 0) });
            settings.Controls.Add(units);
            hatch.Margin = new Padding(14, 5, 3, 0);
            settings.Controls.Add(hatch);
            overlay.Margin = new Padding(14, 5, 3, 0);
            settings.Controls.Add(overlay);
            layout.Controls.Add(settings, 0, 4);
            layout.SetColumnSpan(settings, 3);

            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(990, 360), SplitterDistance = 475 };
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            left.Controls.Add(grid, 0, 0);
            left.Controls.Add(parkingStatus, 0, 1);
            left.Controls.Add(parkingLevels, 0, 2);
            var parkingActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var addParking = new Button { Text = "Другой DWG…", AutoSize = true };
            var clearParking = new Button { Text = "Убрать доп. DWG", AutoSize = true };
            addParking.Click += (_, __) => PickParking();
            clearParking.Click += (_, __) => { RemoveParking(); RefreshZones(); };
            parkingActions.Controls.Add(addParking);
            parkingActions.Controls.Add(clearParking);
            left.Controls.Add(parkingActions, 0, 3);
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(preview);
            layout.Controls.Add(split, 0, 5);
            layout.SetColumnSpan(split, 3);
            layout.Controls.Add(summary, 0, 6);
            layout.SetColumnSpan(summary, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var apply = new Button { Text = "Применить", Width = 124, Height = 32 };
            var cancel = new Button { Text = "Отмена", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
            var markText = new Button { Text = "Подписи разрезов / фасадов…", Width = 216, Height = 32 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(apply);
            buttons.Controls.Add(markText);
            layout.Controls.Add(buttons, 0, 7);
            layout.SetColumnSpan(buttons, 3);
            AcceptButton = apply;
            CancelButton = cancel;

            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Вкл.", FillWeight = 17 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Название постройки", FillWeight = 85 });
            grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Контур", FillWeight = 40 });
            schemes.SelectedIndexChanged += (_, __) => ChangeZone();
            useZone.CheckedChanged += (_, __) =>
            {
                if (changing || zone == null) return;
                if (useZone.Checked) selected.Add(zone);
                else selected.Remove(zone);
                RefreshParkingLevels();
            };
            parkingLevels.ItemCheck += (_, e) =>
            {
                if (changing) return;
                var entry = (ParkingEntry)parkingLevels.Items[e.Index];
                bool enabled = e.NewValue == CheckState.Checked;
                if (enabled) selected.Add(entry.Zone);
                else selected.Remove(entry.Zone);
                if (zone == entry.Zone)
                {
                    changing = true;
                    useZone.Checked = enabled;
                    changing = false;
                }
                UpdateParkingStatus();
            };
            parkingLevels.DoubleClick += (_, __) =>
            {
                if (parkingLevels.SelectedItem is ParkingEntry entry)
                    schemes.SelectedIndex = zones.IndexOf(entry.Zone);
            };
            grid.CurrentCellDirtyStateChanged += (_, __) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += (_, __) => preview.Invalidate();
            grid.SelectionChanged += (_, __) => preview.Invalidate();
            grid.RowsRemoved += (_, __) => preview.Invalidate();
            preview.Paint += DrawPreview;
            apply.Click += (_, __) => Apply();
            markText.Click += (_, __) => EditMarkText();
            RefreshParkingLevels();

            void AddRow(int index, string label, Control entry, string action, Action click)
            {
                layout.Controls.Add(new Label { Text = label, AutoSize = true }, 0, index);
                layout.Controls.Add(entry, 1, index);
                var button = new Button { Text = action, Dock = DockStyle.Fill };
                button.Click += (_, __) => click();
                layout.Controls.Add(button, 2, index);
            }
        }

        public string SourcePath => source.Text;
        public string ParkingPath => parkingZones.Any(selected.Contains) ? parkingSource : null;
        public long ParkingLength { get; private set; }
        public DateTime ParkingWriteTime { get; private set; }
        public string TemplatePath => template.Text;
        public bool NativeMarkText { get; private set; } = true;
        public double FeetPerUnit => (double)units.Value / (double)scale.Value / 304.8;
        
        public List<SchemePart> Parts { get; private set; }
        

        private void PickDrawing()
        {
            using (var dialog = new OpenFileDialog { Filter = "Чертёж AutoCAD (*.dwg)|*.dwg", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    UseWaitCursor = true;
                    using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var next = DwgProcess.Read(dialog.FileName);
                    var nextZones = Scheme.FindZones(next);
                    if (nextZones.Count == 0) throw new InvalidOperationException("В DWG нет поддерживаемых объектов пространства модели.");
                    source.Text = dialog.FileName;
                    SourceLength = new FileInfo(dialog.FileName).Length;
                    SourceWriteTime = File.GetLastWriteTimeUtc(dialog.FileName);
                    drawing = next;
                    markEdits.Clear();
                    RemoveParking();
                    zones = nextZones;
                    zone = null;
                    choices.Clear();
                    selected.Clear();
                    selected.Add(zones[0]);
                    selected.UnionWith(zones.Skip(1).Where(z => Composition.IsParkingBoard(z) ||
                        (!Composition.IsParkingBoard(zones[0]) && Composition.IsAnnotationBoard(z) &&
                            Composition.HasMatchingFootprint(zones[0], z))));
                    units.Value = next.MillimetersPerUnit > 0
                        ? Math.Max(units.Minimum, Math.Min(units.Maximum, (decimal)next.MillimetersPerUnit)) : 1m;
                    RefreshZones();
                }
                catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { UseWaitCursor = false; }
            }
        }

        public long SourceLength { get; private set; }
        public DateTime SourceWriteTime { get; private set; }

        private void EditMarkText()
        {
            var labels = zones.Where(z => !parkingZones.Contains(z)).SelectMany(z => Composition.IsAnnotationBoard(z)
                ? Composition.Marks(z).SelectMany(p => p.Items).Where(Composition.IsMarkText)
                : z.Items.Where(Composition.IsMarkText))
                .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            if (labels.Length == 0)
            {
                MessageBox.Show(this, "Выберите DWG, содержащий подписи разрезов или фасадов.", Text);
                return;
            }
            using (var dialog = new MarkTextDialog(labels, markEdits, NativeMarkText))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                markEdits = dialog.Edits;
                NativeMarkText = dialog.NativeText;
            }
        }

        private void PickParking()
        {
            if (drawing == null)
            {
                MessageBox.Show(this, "Сначала выберите основной DWG.", Text);
                return;
            }
            using (var dialog = new OpenFileDialog { Filter = "Чертёж AutoCAD (*.dwg)|*.dwg", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (string.Equals(dialog.FileName, SourcePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Участки основного DWG уже доступны в списке «Схема в файле».");
                    UseWaitCursor = true;
                    using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var next = DwgProcess.Read(dialog.FileName);
                    var candidates = Scheme.FindZones(next);
                    var recognized = candidates.Where(Composition.IsParkingBoard).ToArray();
                    var available = recognized.Length > 0 ? recognized : candidates.Where(z => z.Outlines.Count > 0).ToArray();
                    if (available.Length == 0) throw new InvalidOperationException("В дополнительном DWG не найден контур паркинга.");
                    RemoveParking();
                    parkingSource = dialog.FileName;
                    ParkingLength = file.Length;
                    ParkingWriteTime = File.GetLastWriteTimeUtc(dialog.FileName);
                    parkingDrawing = next;
                    parkingZones.AddRange(available);
                    zones.AddRange(available);
                    selected.UnionWith(recognized);
                    RefreshZones(available[0]);
                }
                catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { UseWaitCursor = false; }
            }
        }

        private void RemoveParking()
        {
            if (parkingZones.Contains(zone)) zone = null;
            foreach (var candidate in parkingZones)
            {
                zones.Remove(candidate);
                choices.Remove(candidate);
                selected.Remove(candidate);
            }
            parkingZones.Clear();
            parkingSource = null;
            parkingDrawing = null;
        }

        private void RefreshZones(Zone focus = null)
        {
            changing = true;
            schemes.Items.Clear();
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                string names = string.Join(", ", z.Buildings.Select(b => b.Name).Distinct());
                if (Composition.IsAnnotationBoard(z))
                    names = z.Items.Any(x => x.Role == Role.Section && x.Text?.StartsWith("Фасад") == true)
                        ? "Фасады" : "Разрезы";
                string prefix = parkingZones.Contains(z) ? "Паркинг DWG · " : "";
                schemes.Items.Add((i + 1) + " · " + prefix + (names.Length == 0 ? "без подписей" : names) +
                    $"  [X {z.Bounds.Center.X:0}, Y {z.Bounds.Center.Y:0}]");
            }
            changing = false;
            if (zones.Count > 0) schemes.SelectedIndex = Math.Max(0, zones.IndexOf(focus ?? zone));
        }

        private void RefreshParkingLevels()
        {
            changing = true;
            parkingLevels.Items.Clear();
            foreach (var candidate in zones)
            {
                var bindings = choices.TryGetValue(candidate, out var saved) ? saved : candidate.Buildings;
                var names = bindings.Where(b => b.Selected && b.Name != null && Composition.IsParking(b.Name))
                    .Select(b => Visibility.BuildingParameterName(b.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Length == 0) continue;
                string origin = parkingZones.Contains(candidate) ? Path.GetFileName(parkingSource) : "основной DWG";
                parkingLevels.Items.Add(new ParkingEntry(candidate, names, string.Join(", ", names) + " · " + origin),
                    selected.Contains(candidate));
            }
            changing = false;
            UpdateParkingStatus();
        }

        private void UpdateParkingStatus()
        {
            int count = parkingLevels.Items.Cast<ParkingEntry>().Where(e => selected.Contains(e.Zone)).SelectMany(e => e.Levels)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            parkingStatus.Text = parkingLevels.Items.Count == 0 ? "Паркинг · уровни в DWG не найдены" :
                "Паркинг · уровней включено в построение: " + count;
        }

        private sealed class ParkingEntry
        {
            public ParkingEntry(Zone zone, string[] levels, string caption)
            { Zone = zone; Levels = levels; Caption = caption; }
            public Zone Zone { get; }
            public string[] Levels { get; }
            private string Caption { get; }
            public override string ToString() => Caption;
        }

        private void PickTemplate()
        {
            using (var dialog = new OpenFileDialog { Filter = "Шаблон Revit (*.rft)|*.rft", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) template.Text = dialog.FileName;
        }

        private void ChangeZone()
        {
            if (changing || schemes.SelectedIndex < 0) return;
            if (zone != null) choices[zone] = ReadBindings(false);
            zone = zones[schemes.SelectedIndex];
            changing = true;
            useZone.Checked = selected.Contains(zone);
            changing = false;
            grid.Rows.Clear();
            var options = ((DataGridViewComboBoxColumn)grid.Columns[2]).Items;
            options.Clear();
            options.Add("—");
            for (int i = 0; i < zone.Outlines.Count; i++) options.Add(OutlineName(i));
            foreach (var b in choices.TryGetValue(zone, out var saved) ? saved : zone.Buildings)
                grid.Rows[grid.Rows.Add(b.Selected, b.Name, b.Outline == null ? "—" : OutlineName(zone.Outlines.IndexOf(b.Outline)))].Tag = b;
            var currentDrawing = parkingZones.Contains(zone) ? parkingDrawing : drawing;
            string skipped = string.Join(", ", currentDrawing.Skipped.Select(kv => kv.Key + " × " + kv.Value));
            summary.Text = "Проверьте контуры на схеме. Для ручного назначения добавьте строку с названием и номером контура." +
                Environment.NewLine + string.Join(" · ", zone.Items.GroupBy(i => i.Role).OrderBy(g => g.Key)
                    .Select(g => RoleName(g.Key) + " " + g.Count())) +
                (parkingZones.Contains(zone) ? Environment.NewLine +
                    "Для контура без подписи добавьте «Паркинг -1», назначьте контур и включите схему." : "") +
                (currentDrawing.MillimetersPerUnit == 0 ? Environment.NewLine + "DWG без единиц: используется поле «Мм в единице DWG»." : "") +
                (skipped.Length == 0 ? "" : Environment.NewLine + "Пропущено в DWG: " + skipped);
            preview.Invalidate();
            RefreshParkingLevels();
        }

        private string OutlineName(int index) => "№ " + (index + 1) + " · " + zone.Outlines[index].Layer;

        private CadItem ReadOutline(DataGridViewRow row)
        {
            string value = row.Cells[2].Value as string;
            for (int i = 0; i < zone.Outlines.Count; i++)
                if (value == OutlineName(i)) return zone.Outlines[i];
            return null;
        }

        private void DrawPreview(object sender, PaintEventArgs e)
        {
            if (zone == null || zone.Items.Count == 0) return;
            var axes = new SchemePart(Role.Axis, Array.Empty<string>());
            axes.Items.AddRange(zone.Items.Where(i => i.Role == Role.Axis));
            Composition.FormatAxes(axes);
            var items = zone.Items.Where(i => i.Role != Role.Axis).Concat(axes.Items).ToArray();
            var bounds = Core.Bounds.From(items.SelectMany(i => i.Points));
            float factor = (float)Math.Min((preview.Width - 26) / Math.Max(1, bounds.Width),
                (preview.Height - 26) / Math.Max(1, bounds.Height));
            if (factor <= 0) return;
            PointF Screen(Point2 p) => new PointF(preview.Width / 2f + (float)(p.X - bounds.Center.X) * factor,
                preview.Height / 2f - (float)(p.Y - bounds.Center.Y) * factor);
            var focused = grid.CurrentRow == null ? null : ReadOutline(grid.CurrentRow);
            var assigned = grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && (bool?)r.Cells[0].Value != false)
                .Select(ReadOutline).Where(i => i != null).ToHashSet();
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var basePen = new Pen(Color.LightSlateGray))
            using (var axisPen = new Pen(Color.IndianRed) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            using (var buildingPen = new Pen(Color.LightSkyBlue, 1.6f))
            using (var activePen = new Pen(Color.Gold, 2.6f))
            {
                foreach (var item in items.Where(i => i.Text == null).OrderBy(i => i == focused))
                {
                    var points = item.Points.Select(Screen).ToArray();
                    var pen = item == focused ? activePen : assigned.Contains(item) ? buildingPen : item.Role == Role.Axis ? axisPen : basePen;
                    if (item.Closed) e.Graphics.DrawPolygon(pen, points);
                    else e.Graphics.DrawLines(pen, points);
                }
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    foreach (var item in axes.Items.Where(i => i.CenterText))
                    {
                        using (var font = new Font(Font.FontFamily, Math.Max(5, (float)item.Height * factor), GraphicsUnit.Pixel))
                            e.Graphics.DrawString(item.Text, font, Brushes.White, Screen(item.Points[0]), format);
                    }
                for (int i = 0; i < zone.Outlines.Count; i++)
                {
                    var item = zone.Outlines[i];
                    var center = Screen(item.Bounds.Center);
                    e.Graphics.DrawString((i + 1).ToString(), Font, item == focused ? Brushes.Gold : Brushes.White, center);
                }
            }
        }

        private void Apply()
        {
            try
            {
                if (zone == null) throw new InvalidOperationException("Выберите исходный DWG.");
                if (!File.Exists(TemplatePath)) throw new InvalidOperationException("Выберите RFT «Типовая аннотация».");
                if (selected.Count == 0) throw new InvalidOperationException("Включите хотя бы одну схему.");
                choices[zone] = ReadBindings(selected.Contains(zone));
                var enabledZones = zones.Where(selected.Contains).ToArray();
                foreach (var current in enabledZones)
                {
                    if (choices.TryGetValue(current, out var bindings))
                    {
                        ValidateBindings(bindings);
                        current.Buildings.Clear();
                        current.Buildings.AddRange(bindings);
                    }
                    if (parkingZones.Contains(current) && (!Composition.IsParkingBoard(current) ||
                        current.Buildings.Any(b => b.Selected && b.Outline == null)))
                        throw new InvalidOperationException("Для каждой включённой схемы из DWG паркинга задайте название уровня и контур.");
                }
                var primary = enabledZones.FirstOrDefault(z => !parkingZones.Contains(z) &&
                    !Composition.IsAnnotationBoard(z) && !Composition.IsParkingBoard(z))
                    ?? enabledZones.FirstOrDefault(z => !parkingZones.Contains(z))
                    ?? throw new InvalidOperationException("Включите основную схему.");
                var origin = new Point2(primary.Bounds.Left, primary.Bounds.Bottom);
                var externalLevels = enabledZones.Where(parkingZones.Contains).SelectMany(z => z.Buildings)
                    .Where(b => b.Selected).Select(b => Visibility.BuildingParameterName(b.Name)).ToArray();
                if (externalLevels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != externalLevels.Length)
                    throw new InvalidOperationException("В отдельном DWG выберите один контур для каждого уровня паркинга.");
                var overrides = enabledZones.Where(z => !parkingZones.Contains(z)).SelectMany(z => z.Buildings)
                    .Where(b => Composition.IsParking(b.Name) && externalLevels.Contains(Visibility.BuildingParameterName(b.Name),
                        StringComparer.OrdinalIgnoreCase)).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Parts = new List<SchemePart>();
                foreach (var current in enabledZones)
                {
                    bool external = parkingZones.Contains(current);
                    bool parking = Composition.IsParkingBoard(current);
                    double factor = external && parkingDrawing.MillimetersPerUnit > 0 ?
                        parkingDrawing.MillimetersPerUnit / (double)units.Value : 1;
                    bool match = current != primary && Composition.HasMatchingFootprint(primary, current, factor);
                    bool aligned = overlay.Checked && current != primary && (match || parking);
                    bool contoursOnly = parking && ((aligned && match) ||
                        (external && current.Buildings.All(b => b.Label == null)));
                    var parts = Composition.IsAnnotationBoard(current) ? Composition.Marks(current) :
                        Scheme.Build(current, hatch.Checked, contoursOnly, external ? null : overrides);
                    foreach (var part in parts)
                    {
                        part.Origin = aligned ? Composition.ParkingOrigin(primary, current, origin, factor, match) :
                            origin * (1 / factor);
                        part.UnitFactor = factor;
                        for (int i = 0; i < part.Items.Count; i++)
                        {
                            var item = part.Items[i];
                            if (external || !Composition.IsMarkText(item) || !markEdits.TryGetValue(item.Id, out var edit))
                                continue;
                            var label = item.WithText(edit.Text ?? item.Text, item.Points[0], item.Height, item.Rotation, item.Alignment);
                            label.Role = Composition.MarkTextRole(item);
                            label.TextOffsetMm = edit.Offset;
                            part.Items[i] = label;
                        }
                    }
                    Parts.AddRange(parts);
                }
                if (!Composition.IsAnnotationBoard(primary))
                {
                    var box = primary.Bounds;
                    double height = Math.Max(box.Width, box.Height) * .042;
                    var title = new SchemePart(Role.Title, Array.Empty<string>()) { Origin = origin };
                    title.Items.Add(new CadItem { Role = Role.Title, Text = "Компоновочная схема", Height = height,
                        Alignment = 1, Points = new[] { new Point2(box.Center.X, box.Top + height * 3.8) } });
                    Parts.Add(title);
                }
                var names = Parts.SelectMany(p => p.Buildings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Select(Visibility.BuildingParameterName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                    throw new InvalidOperationException("Два названия дают одинаковое имя параметра. Уточните названия построек.");
                if (Parts.Count == 0) throw new InvalidOperationException("В выбранной схеме нет элементов.");
                DialogResult = DialogResult.OK;
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private List<Building> ReadBindings(bool strict)
        {
            grid.EndEdit();
            var bindings = new List<Building>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                var old = row.Tag as Building;
                string name = (row.Cells[1].Value as string)?.Trim();
                var outline = ReadOutline(row);
                if (!strict && old == null && string.IsNullOrWhiteSpace(name) && outline == null) continue;
                bindings.Add(new Building { Name = name, Label = old?.Label, Outline = outline,
                    Selected = (bool?)row.Cells[0].Value != false });
            }
            if (strict) ValidateBindings(bindings);
            return bindings;
        }

        private static void ValidateBindings(List<Building> bindings)
        {
            if (bindings.Any(b => string.IsNullOrWhiteSpace(b.Name)))
                throw new InvalidOperationException("Заполните название в каждой строке выбранных схем.");
            if (bindings.Any(b => b.Label == null && b.Outline == null))
                throw new InvalidOperationException("Для новой постройки выберите контур.");
            if (bindings.Count != 0 && bindings.All(b => !b.Selected))
                throw new InvalidOperationException("Во включённой схеме должна быть выбрана хотя бы одна постройка.");
            if (bindings.Where(b => b.Outline != null).GroupBy(b => b.Outline).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Назначьте каждому контуру одну постройку.");
        }

        internal static string RoleName(Role role)
        {
            switch (role)
            {
                case Role.Axis: return "Оси";
                case Role.Dimension: return "Размеры";
                case Role.Section: return "Разрезы";
                case Role.Facade: return "Фасады";
                case Role.Title: return "Основная надпись";
                case Role.Text: return "Подписи";
                case Role.Hatch: return "Штриховки";
                default: return "Контуры";
            }
        }
    }
}



