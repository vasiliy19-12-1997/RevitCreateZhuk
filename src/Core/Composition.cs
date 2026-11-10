using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitCreateZhuk.Core
{
    internal static class Composition
    {
        public static bool IsParking(string name) =>
            name.StartsWith("Паркинг", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Автостоянка", StringComparison.OrdinalIgnoreCase);

        public static bool IsAnnex(string name) => name.StartsWith("Пристройка", StringComparison.OrdinalIgnoreCase);

        public static bool HasMatchingFootprint(Zone first, Zone other, double unitFactor = 1)
        {
            var a = Envelope(first);
            var b = Envelope(other);
            if (a == null || b == null || a.Points.Length != b.Points.Length) return false;
            double tolerance = Math.Max(a.Bounds.Width, a.Bounds.Height) * .001;
            return a.Points.All(p => b.Points.Any(q =>
                (p - a.Bounds.Center).DistanceTo((q - b.Bounds.Center) * unitFactor) <= tolerance));
        }

        public static Point2 ParkingOrigin(Zone primary, Zone parking, Point2 origin, double unitFactor, bool match)
        {
            if (match)
                return Envelope(parking).Bounds.Center - (Envelope(primary).Bounds.Center - origin) * (1 / unitFactor);
            var outlines = parking.Buildings.Where(b => b.Selected && b.Outline != null).Select(b => b.Outline).ToArray();
            var bounds = outlines.Length == 0 ? parking.Bounds : Bounds.From(outlines.SelectMany(i => i.Points));
            var source = new Point2(bounds.Left, bounds.Bottom);
            var target = new Point2(primary.Bounds.Left, primary.Bounds.Bottom);
            return source - (target - origin) * (1 / unitFactor);
        }

        public static bool IsAnnotationBoard(Zone zone) =>
            zone.Buildings.Count == 0 && zone.Items.Any(i => i.Role == Role.Section && i.Text != null);

        public static bool IsParkingBoard(Zone zone) =>
            zone.Buildings.Count > 0 && zone.Buildings.All(b => IsParking(b.Name));

        public static CadItem Envelope(Zone zone) => zone.Outlines.Where(i => i.Closed)
            .OrderByDescending(i => Recognition.Area(i.Points)).FirstOrDefault();

        public static List<SchemePart> Marks(Zone zone)
        {
            var labels = zone.Items.Where(i => i.Role == Role.Section && i.Text != null)
                .Select(i => new { Item = i, Part = new SchemePart(
                    MarkTextRole(i),
                    Array.Empty<string>()) { Mark = MarkName(i.Text) } }).ToList();
            var strokes = zone.Items.Where(i => i.Text == null && i.Points.Length == 2 &&
                i.Role == Role.Geometry && !zone.Outlines.Contains(i)).ToList();
            var main = new List<(CadItem Line, SchemePart Part)>();
            var used = new HashSet<CadItem>();
            double size = Math.Max(zone.Bounds.Width, zone.Bounds.Height);
            foreach (var label in labels)
            {
                var item = label.Item;
                var part = label.Part;
                var text = FormatMarkText(item);
                part.Items.Add(item.WithText(text, item.Points[0], item.Height, item.Rotation, item.Alignment));
                if (part.Role != Role.Section || !item.Id.EndsWith("/text", StringComparison.Ordinal)) continue;
                var root = item.Id.Substring(0, item.Id.Length - 5) + "/";
                var leaders = zone.Items.Where(i => i.Text == null && i.Id.StartsWith(root, StringComparison.Ordinal)).ToArray();
                var leader = leaders.FirstOrDefault(i => i.Points.Length == 2);
                if (leader == null) continue;
                var line = strokes.Where(i => i.Points[0].DistanceTo(i.Points[1]) > size * .45 && !used.Contains(i))
                    .OrderBy(i => leader.Points.Min(p => Recognition.Distance(i, p))).FirstOrDefault();
                if (line == null || leader.Points.Min(p => Recognition.Distance(line, p)) > size * .035) continue;
                // The DWG includes a second arrow on the text leader. The section line has its own end marks.
                part.Items.Add(leader);
                part.Items.Add(line);
                used.Add(line);
                main.Add((line, part));
            }
            foreach (var stroke in strokes.Where(i => !used.Contains(i)))
            {
                if (main.Count > 0)
                {
                    var target = main.OrderBy(x => x.Line.Points.Min(p => Recognition.Distance(stroke, p))).First();
                    if (target.Line.Points.Min(p => Recognition.Distance(stroke, p)) < size * .065 &&
                        stroke.Points[0].DistanceTo(stroke.Points[1]) < size * .1)
                        target.Part.Items.Add(stroke);
                }
                else
                {
                    var target = labels.OrderBy(x => Recognition.Distance(stroke, x.Item.Points[0])).FirstOrDefault();
                    if (target != null && Recognition.Distance(stroke, target.Item.Points[0]) < size * .16)
                        target.Part.Items.Add(stroke);
                }
            }
            return labels.Select(x => x.Part).ToList();
        }

        public static bool IsMarkText(CadItem item) =>
            item.Text != null && (item.Role == Role.Section || item.Role == Role.Facade);

        public static string FormatMarkText(CadItem item) => MarkTextRole(item) == Role.Facade
            ? Regex.Replace(item.Text, @"^Фасад\s+в\s+осях\s+", "Фасад в осях\n", RegexOptions.IgnoreCase)
            : item.Text;

        public static Role MarkTextRole(CadItem item) =>
            item.Role == Role.Facade || item.Text.StartsWith("Фасад", StringComparison.OrdinalIgnoreCase)
                ? Role.Facade : Role.Section;

        public static string MarkName(string text) => Regex.Replace(
            Regex.Replace(text.Trim(), @"^Фасад\s+в\s+осях\s+", "Фасад ", RegexOptions.IgnoreCase), @"\s+", " ");

        public static CadItem FormatBuilding(CadItem text, CadItem outline)
        {
            string value = Regex.Replace(text.Text, @"^((?:Корпус|Пристройка|Секция))\s+(?=\S)", "$1\n",
                RegexOptions.IgnoreCase);
            var top = text.Points[0];
            double height = text.Height * .82;
            if (outline == null) return text.WithText(value, top, height, 0, text.Alignment);
            var bounds = outline.Bounds;
            var center = InteriorPoint(outline);
            int lines = value.Count(c => c == '\n') + 1;
            double labelWidth = value.Split('\n').Max(s => s.Length) * height * .48;
            if (bounds.Width < labelWidth * 1.3)
                return text.WithText(value, new Point2(bounds.Left - height * 4, top.Y), height, 0, 2);
            return text.WithText(value, center + new Point2(0, lines * height * .65), height, 0, 1);
        }

        private static Point2 InteriorPoint(CadItem outline)
        {
            var bounds = outline.Bounds;
            var points = outline.Points;
            var border = new CadItem { Points = points.Concat(new[] { points[0] }).ToArray() };
            var best = bounds.Center;
            double bestDistance = -1;
            for (int x = 1; x < 20; x++)
            for (int y = 1; y < 20; y++)
            {
                var candidate = new Point2(bounds.Left + bounds.Width * x / 20, bounds.Bottom + bounds.Height * y / 20);
                if (!Recognition.Contains(points, candidate)) continue;
                double distance = Recognition.Distance(border, candidate);
                if (distance <= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        public static void FormatAxes(SchemePart part)
        {
            var rings = part.Items.Where(i => i.Text == null && i.Id.Contains("/") && i.Points.Length > 10)
                .GroupBy(i => i.Id.Split('/')[0]).Where(g => g.Count() >= 2)
                .Select(g => new AxisRing(g.ToArray())).Where(r => r.IsCircular).ToArray();
            if (rings.Length == 0) return;

            var paired = new HashSet<AxisRing>();
            foreach (var inner in rings.OrderBy(r => r.SourceRadius))
            {
                if (paired.Contains(inner)) continue;
                var outer = rings.Where(r => r.SourceRadius > inner.SourceRadius * 1.15 && !paired.Contains(r))
                    .Where(r => ArePaired(inner, r)).OrderBy(r => r.SourceCenter.DistanceTo(inner.SourceCenter)).FirstOrDefault();
                if (outer == null) continue;
                paired.Add(inner);
                paired.Add(outer);
                inner.Radius = outer.Radius = inner.SourceRadius * .57;
                var direction = outer.SourceCenter - inner.SourceCenter;
                outer.Center = inner.Center + direction * ((inner.Radius + outer.Radius) / direction.Length);
            }

            var text = part.Items.Where(i => i.Text != null).ToArray();
            var claimed = new HashSet<CadItem>();
            foreach (var ring in rings)
            {
                var label = text.Where(i => !claimed.Contains(i) &&
                        i.Points[0].DistanceTo(ring.SourceCenter) <= ring.SourceRadius * 1.08)
                    .OrderBy(i => i.Points[0].DistanceTo(ring.SourceCenter)).FirstOrDefault();
                if (label != null)
                {
                    claimed.Add(label);
                    var height = Math.Min(ring.Radius * .82, ring.Radius * 2.7 / Math.Max(2, label.Text.Length));
                    var centered = label.WithText(label.Text, ring.Center, height, 0, 1);
                    centered.CenterText = true;
                    part.Items[part.Items.IndexOf(label)] = centered;
                }

                part.Items.RemoveAll(i => ring.Items.Contains(i));
                part.Items.Add(new CadItem
                {
                    Id = ring.Items[0].Id, Role = Role.Axis, Layer = ring.Items[0].Layer, Closed = true, Round = true,
                    Points = Enumerable.Range(0, 48).Select(k =>
                    {
                        double angle = 2 * Math.PI * k / 48;
                        return ring.Center + new Point2(Math.Cos(angle), Math.Sin(angle)) * ring.Radius;
                    }).ToArray()
                });
            }

            for (int i = 0; i < part.Items.Count; i++)
            {
                var line = part.Items[i];
                if (line.Text != null || line.Points.Length != 2) continue;
                var points = line.Points.ToArray();
                for (int end = 0; end < 2; end++)
                {
                    var near = line.Points[end];
                    var far = line.Points[1 - end];
                    bool vertical = Math.Abs(near.X - far.X) < 1e-7;
                    bool horizontal = Math.Abs(near.Y - far.Y) < 1e-7;
                    var ring = rings.Where(r =>
                        {
                            double distance = near.DistanceTo(r.SourceCenter);
                            double tolerance = r.SourceRadius * .035;
                            return Math.Abs(distance - r.SourceRadius) <= tolerance &&
                                far.DistanceTo(r.SourceCenter) > distance &&
                                (!vertical || Math.Abs(near.X - r.SourceCenter.X) <= tolerance) &&
                                (!horizontal || Math.Abs(near.Y - r.SourceCenter.Y) <= tolerance);
                        })
                        .OrderBy(r => Math.Abs(near.DistanceTo(r.SourceCenter) / r.SourceRadius - 1)).FirstOrDefault();
                    if (ring == null) continue;
                    if (vertical || horizontal)
                    {
                        double offset = vertical ? near.X - ring.Center.X : near.Y - ring.Center.Y;
                        if (Math.Abs(offset) >= ring.Radius) continue;
                        double reach = Math.Sqrt(ring.Radius * ring.Radius - offset * offset);
                        points[end] = vertical
                            ? new Point2(near.X, ring.Center.Y + Math.Sign(far.Y - ring.Center.Y) * reach)
                            : new Point2(ring.Center.X + Math.Sign(far.X - ring.Center.X) * reach, near.Y);
                        continue;
                    }
                    var direction = far - ring.Center;
                    points[end] = ring.Center + direction * (ring.Radius / direction.Length);
                }
                part.Items[i] = line.WithPoints(points);
            }
        }

        private static bool ArePaired(AxisRing inner, AxisRing outer)
        {
            var delta = outer.SourceCenter - inner.SourceCenter;
            double total = inner.SourceRadius + outer.SourceRadius;
            return delta.Length >= total * .85 && delta.Length <= total * 1.15 &&
                Math.Min(Math.Abs(delta.X), Math.Abs(delta.Y)) < total * .07;
        }

        private sealed class AxisRing
        {
            public AxisRing(CadItem[] items)
            {
                Items = items;
                var bounds = Bounds.From(items.SelectMany(i => i.Points));
                SourceCenter = Center = bounds.Center;
                SourceRadius = bounds.Width / 2;
                Radius = SourceRadius * .46;
                IsCircular = SourceRadius > 0 && Math.Abs(bounds.Width - bounds.Height) < bounds.Width * .03;
            }

            public CadItem[] Items { get; }
            public Point2 SourceCenter { get; }
            public double SourceRadius { get; }
            public bool IsCircular { get; }
            public Point2 Center { get; set; }
            public double Radius { get; set; }
        }
    }
}
