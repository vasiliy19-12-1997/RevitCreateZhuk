using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk
{
    internal sealed class AnnotationGraphics
    {

        private readonly double primaryFeetPerUnit;
        private readonly string drawingFont;

        public AnnotationGraphics(double feetPerUnit)
        {

            primaryFeetPerUnit = feetPerUnit;
            using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                drawingFont = new[] { "GOST Common", "GOST type B", "ISOCPEUR", "Arial Narrow" }
                    .FirstOrDefault(name => fonts.Families.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        public int SkippedItems { get; private set; }
        public int WrittenItems { get; private set; }

        private sealed class VisibilityUnavailableException : Exception { }

        public bool TryWriteNative(Document doc, View view, SchemePart part, Func<Element, bool> bind)
        {
            using (var trial = new SubTransaction(doc))
            {
                if (trial.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Не удалось начать построение элементов схемы.");
                int written = WrittenItems, skipped = SkippedItems;
                try
                {
                    Write(doc, view, part, bind);
                    if (trial.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit отменил построение элементов схемы.");
                    return true;
                }
                catch (VisibilityUnavailableException)
                {
                    trial.RollBack();
                    WrittenItems = written;
                    SkippedItems = skipped;
                    return false;
                }
            }
        }

        public bool Write(Document doc, View view, SchemePart part, Func<Element, bool> bind = null)
        {
            double feetPerUnit = primaryFeetPerUnit * part.UnitFactor;
            double tolerance = doc.Application.ShortCurveTolerance * 1.001;
            var basis = view.Origin;
            var right = view.RightDirection;
            var up = view.UpDirection;
            var style = part.Items.Any(i => i.Text == null) ? CreateStyle(doc, part.Role) : null;
            bool madeAnything = WriteLabels(doc, view, new[] { part }, Register) > 0;
            foreach (var item in part.Items.Where(i => i.Text == null))
            {
                bool created = false;
                if (item.Role == Role.Hatch)
                {
                    created = DrawHatch(item);
                }
                else if (item.Round && item.Bounds.Width * feetPerUnit * Math.PI / 2 > tolerance)
                {
                    var center = Map(item.Bounds.Center);
                    double radius = item.Bounds.Width * feetPerUnit / 2;
                    var left = center - right * radius;
                    var rightmost = center + right * radius;
                    AddCurve(Arc.Create(left, rightmost, center + up * radius));
                    AddCurve(Arc.Create(rightmost, left, center - up * radius));
                    created = true;
                }
                else
                {
                    var points = item.Points.Select(Map).ToArray();
                    XYZ start = points[0];
                    for (int index = 1; index < points.Length + (item.Closed ? 1 : 0); index++)
                    {
                        XYZ end = points[index % points.Length];
                        if (start.DistanceTo(end) < tolerance) continue;
                        AddLine(start, end);
                        start = end;
                        created = true;
                    }
                }
                if (created) { WrittenItems++; madeAnything = true; }
                else SkippedItems++;
            }
            return madeAnything;

            XYZ Map(Point2 p) => basis + right * ((p.X - part.Origin.X) * feetPerUnit) +
                up * ((p.Y - part.Origin.Y) * feetPerUnit);

            void AddLine(XYZ start, XYZ end) => AddCurve(Line.CreateBound(start, end));

            void AddCurve(Curve curve)
            {
                // Generic annotations use the view's plane for detail lines.
                var detail = doc.FamilyCreate.NewDetailCurve(view, curve);
                if (detail == null)
                    throw new InvalidOperationException("Revit не создал линию на виде типовой аннотации «" + view.Name + "».");
                detail.LineStyle = style;
                Register(detail);
            }

            void Register(Element element)
            {
                if (bind != null && !bind(element)) throw new VisibilityUnavailableException();
            }

            bool DrawHatch(CadItem item)
            {
                var loops = new[] { item.Points }.Concat(item.Holes ?? Array.Empty<Point2[]>()).ToArray();
                double diagonal = Math.Sqrt(2);
                double minimum = loops.SelectMany(p => p).Min(p => p.Y - p.X);
                double maximum = loops.SelectMany(p => p).Max(p => p.Y - p.X);
                double spacing = Math.Max(1.5 / (304.8 * feetPerUnit) * diagonal, (maximum - minimum) / 1000);
                bool created = false;
                for (double intercept = minimum + spacing * .5; intercept < maximum; intercept += spacing)
                {
                    var cuts = new List<double>();
                    foreach (var loop in loops)
                        for (int i = 0; i < loop.Length; i++)
                        {
                            var a = loop[i];
                            var b = loop[(i + 1) % loop.Length];
                            double da = a.Y - a.X - intercept, db = b.Y - b.X - intercept;
                            if ((da > 0) == (db > 0)) continue;
                            cuts.Add(a.X + (b.X - a.X) * da / (da - db));
                        }
                    cuts.Sort();
                    for (int i = 1; i < cuts.Count; i += 2)
                    {
                        if ((cuts[i] - cuts[i - 1]) * feetPerUnit * diagonal < tolerance) continue;
                        var start = new Point2(cuts[i - 1], cuts[i - 1] + intercept);
                        var end = new Point2(cuts[i], cuts[i] + intercept);
                        AddLine(Map(start), Map(end));
                        created = true;
                    }
                }
                return created;
            }
        }

        public int WriteLabels(Document document, View view, IEnumerable<SchemePart> parts, Action<TextNote> created = null)
        {
            var entries = parts.SelectMany(part => part.Items.Where(i => i.Text != null)
                .Select(item => (Part: part, Item: item))).ToArray();
            if (entries.Length == 0) return 0;

            var types = new FilteredElementCollector(document).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .ToDictionary(type => type.Name, StringComparer.OrdinalIgnoreCase);
            var prototype = document.GetElement(document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType
                ?? types.Values.FirstOrDefault();
            if (prototype == null) throw new InvalidOperationException("В семействе отсутствует тип текста.");

            foreach (var entry in entries)
            {
                var item = entry.Item;
                var part = entry.Part;
                Role role = Composition.IsMarkText(item) ? Composition.MarkTextRole(item) : part.Role;
                int size = (int)Math.Round(Math.Max(.8, Math.Min(25,
                    item.Height * primaryFeetPerUnit * part.UnitFactor * 304.8)) * 10);
                string name = "Жук_Текст_" + role + "_" + size;
                if (!types.TryGetValue(name, out var type))
                {
                    type = (TextNoteType)prototype.Duplicate(name);
                    type.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(size / 3048.0);
                    var background = type.get_Parameter(BuiltInParameter.TEXT_BACKGROUND);
                    if (background != null && !background.IsReadOnly) background.Set(1);
                    if (drawingFont != null) type.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(drawingFont);
                    var color = DrawingColor(role);
                    if (color != null)
                    {
                        var ink = type.get_Parameter(BuiltInParameter.LINE_COLOR) ?? type.get_Parameter(BuiltInParameter.TEXT_COLOR);
                        if (ink != null && !ink.IsReadOnly && ink.StorageType == StorageType.Integer)
                            ink.Set(color.Red | color.Green << 8 | color.Blue << 16);
                    }
                    types.Add(name, type);
                }
                var options = new TextNoteOptions(type.Id)
                {
                    Rotation = item.Rotation,
                    KeepRotatedTextReadable = false,
                    VerticalAlignment = item.CenterText ? VerticalTextAlignment.Middle : VerticalTextAlignment.Top,
                    HorizontalAlignment = item.Alignment == 1 ? HorizontalTextAlignment.Center :
                        item.Alignment == 2 ? HorizontalTextAlignment.Right : HorizontalTextAlignment.Left
                };
                var anchor = part.TextAnchor(item, primaryFeetPerUnit);
                var position = view.Origin + view.RightDirection * anchor.X + view.UpDirection * anchor.Y;
                var note = TextNote.Create(document, view.Id, position, item.Text, options);
                created?.Invoke(note);
                WrittenItems++;
            }
            return entries.Length;
        }

        private static GraphicsStyle CreateStyle(Document document, Role role)
        {
            var categories = document.Settings.Categories;
            var parent = document.OwnerFamily.FamilyCategory;
            string name = "Жук_" + SchemeDialog.RoleName(role);
            var category = parent.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == name);
            if (category != null) return category.GetGraphicsStyle(GraphicsStyleType.Projection);
            category = categories.NewSubcategory(parent, name);
            category.SetLineWeight(1, GraphicsStyleType.Projection);
            var color = DrawingColor(role);
            if (color != null) category.LineColor = color;
            category.SetLinePatternId(LinePatternElement.GetSolidPatternId(), GraphicsStyleType.Projection);
            return category.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        private static Color DrawingColor(Role role)
        {
            if (role == Role.Section) return new Color(220, 30, 40);
            if (role == Role.Facade) return new Color(0, 140, 55);
            return null;
        }
    }
}
