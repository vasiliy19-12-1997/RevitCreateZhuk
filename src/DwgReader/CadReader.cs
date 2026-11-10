using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk.DwgReader
{
    internal sealed class CadReader
    {
        private readonly Drawing drawing = new Drawing();

        public Drawing Read(string path)
        {
            var source = ACadSharp.IO.DwgReader.Read(path);
            drawing.MillimetersPerUnit = Millimeters((int)source.Header.InsUnits);
            foreach (var entity in source.Entities)
                Visit(entity, p => p, "", null, Role.Geometry, 0);
            return drawing;
        }

        private static double Millimeters(int unit)
        {
            switch (unit)
            {
                case 0: return 0;
                case 1: return 25.4;
                case 2: return 304.8;
                case 3: return 1609344;
                case 4: return 1;
                case 5: return 10;
                case 6: return 1000;
                case 7: return 1e6;
                case 8: return .0000254;
                case 9: return .0254;
                case 10: return 914.4;
                case 11: return 1e-7;
                case 12: return 1e-6;
                case 13: return .001;
                case 14: return 100;
                case 15: return 1e4;
                case 16: return 1e5;
                case 21: return 1200000d / 3937;
                default: return 0;
            }
        }

        private void Visit(Entity entity, Func<Point2, Point2> transform, string parentId,
            string parentLayer, Role parentRole, int depth)
        {
            if (depth > 32) { Skip("глубина вложенности блоков"); return; }
            if (entity.IsInvisible || entity.Layer?.IsOn == false) return;
            string layer = entity.Layer?.Name ?? "0";
            if (layer == "0" && parentLayer != null) layer = parentLayer;
            string id = parentId + entity.Handle.ToString("X");
            Role role = parentRole == Role.Geometry ? Recognition.LayerRole(layer) : parentRole;

            void Shape(IEnumerable<Point2> points, bool closed = false) =>
                Add(new CadItem { Id = id, Layer = layer, Role = role, Closed = closed,
                    Points = points.Select(transform).ToArray() });

            void Text(string value, Point2 position, double rotation, double height, int alignment)
            {
                value = CleanText(value);
                if (string.IsNullOrWhiteSpace(value)) return;
                var start = transform(position);
                var along = transform(position + new Point2(Math.Cos(rotation), Math.Sin(rotation)));
                var up = transform(position + new Point2(-Math.Sin(rotation), Math.Cos(rotation)) * height);
                var vector = along - start;
                if (role == Role.Geometry && Recognition.LayerRole(value) == Role.Section) role = Role.Section;
                Add(new CadItem { Id = id, Layer = layer, Role = role == Role.Geometry || Recognition.BuildingName(value) != null ? Role.Text : role,
                    Points = new[] { start }, Text = value, Height = start.DistanceTo(up),
                    Rotation = Math.Atan2(vector.Y, vector.X), Alignment = alignment });
            }

            switch (entity)
            {
                case Insert block when block.Block != null:
                    if (block.Normal.Z < .999 || block.IsMultiple) { Skip("массив или наклонный блок"); return; }
                    var offset = Point(block.InsertPoint);
                    var basis = Point(block.Block.BlockEntity.BasePoint);
                    double cos = Math.Cos(block.Rotation), sin = Math.Sin(block.Rotation);
                    var blockRole = role == Role.Geometry ? Recognition.LayerRole(block.Block.Name) : role;
                    Point2 TransformBlock(Point2 p)
                    {
                        var local = p - basis;
                        double x = local.X * block.XScale, y = local.Y * block.YScale;
                        return transform(offset + new Point2(x * cos - y * sin, x * sin + y * cos));
                    }
                    foreach (var child in block.Block.Entities.Where(child => !(child is AttributeDefinition)))
                        Visit(child, TransformBlock, id + "/", layer, blockRole, depth + 1);
                    foreach (var attribute in block.Attributes)
                        Visit(attribute, transform, id + "/", layer, blockRole, depth + 1);
                    break;
                case Dimension dim when dim.Block != null && dim.Block.Entities.Any():
                    foreach (var child in dim.Block.Entities)
                        Visit(child, transform, id + "/", layer, Role.Dimension, depth + 1);
                    break;
                case DimensionAligned dim when dim.Normal.Z > .999:
                    var a = Point(dim.FirstPoint); var b = Point(dim.SecondPoint);
                    var direction = b - a;
                    if (dim is DimensionLinear linear)
                        direction = new Point2(Math.Cos(linear.Rotation), Math.Sin(linear.Rotation));
                    if (direction.Length < 1e-9) { Skip("нулевой размер"); break; }
                    direction *= 1 / direction.Length;
                    var normal = new Point2(-direction.Y, direction.X);
                    var reference = Point(dim.DefinitionPoint);
                    double Offset(Point2 p) => (reference.X - p.X) * normal.X + (reference.Y - p.Y) * normal.Y;
                    var da = a + normal * Offset(a); var db = b + normal * Offset(b);
                    role = Role.Dimension;
                    Shape(new[] { a, da, db, b });
                    string measurement = dim.Measurement.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    string label = string.IsNullOrEmpty(dim.Text) ? measurement : dim.Text.Replace("<>", measurement);
                    id += "/text";
                    Text(label, (da + db) * .5, Math.Atan2(direction.Y, direction.X),
                        dim.Style?.TextHeight ?? 2.5, 1);
                    break;
                case MultiLeader leader when leader.ContextData != null:
                    var context = leader.ContextData;
                    if (context.HasContentsBlock) Skip("блок в мультивыноске");
                    if (Recognition.LayerRole(CleanText(context.TextLabel)) == Role.Section) role = Role.Section;
                    string rootId = id;
                    int lineNumber = 0;
                    foreach (var root in context.LeaderRoots)
                    {
                        foreach (var leaderLine in root.Lines)
                        {
                            id = rootId + "/leader" + lineNumber++;
                            var points = leaderLine.Points.Select(Point).ToList();
                            if (points.Count > 0 && points.Last().DistanceTo(Point(root.ConnectionPoint)) > 1e-7)
                                points.Add(Point(root.ConnectionPoint));
                            Shape(points);
                            if (points.Count >= 2 && context.ArrowheadSize > 0)
                            {
                                var vector = points[1] - points[0];
                                if (vector.Length > 1e-7)
                                {
                                    vector *= Math.Min(context.ArrowheadSize, vector.Length / 2) / vector.Length;
                                    var wing = new Point2(-vector.Y, vector.X) * .3;
                                    id += "/arrow";
                                    Shape(new[] { points[0] + vector + wing, points[0], points[0] + vector - wing });
                                }
                            }
                        }
                    }
                    if (context.HasTextContents)
                    {
                        id = rootId + "/text";
                        Text(context.TextLabel, Point(context.TextLocation), context.TextRotation,
                            context.TextHeight, (int)context.TextAlignment - 1);
                    }
                    break;
                case Leader leader when leader.Normal.Z > .999:
                    Shape(leader.Vertices.Select(Point));
                    break;
                case Ellipse ellipse when ellipse.Normal.Z > .999:
                    var center = Point(ellipse.Center);
                    var major = Point(ellipse.MajorAxisEndPoint);
                    var minor = new Point2(-major.Y, major.X) * ellipse.RadiusRatio;
                    var sweep = ellipse.IsFullEllipse ? 2 * Math.PI : Sweep(ellipse.StartParameter, ellipse.EndParameter);
                    Shape(ArcPoints(new Point2(0, 0), 1, ellipse.StartParameter, sweep, !ellipse.IsFullEllipse)
                        .Select(p => center + major * p.X + minor * p.Y), ellipse.IsFullEllipse);
                    break;
                case Solid solid when solid.Normal.Z > .999:
                    Shape(new[] { Point(solid.FirstCorner), Point(solid.SecondCorner),
                        Point(solid.FourthCorner), Point(solid.ThirdCorner) }.Distinct(), true);
                    break;
                case Line line when Math.Abs(line.StartPoint.Z - line.EndPoint.Z) < 1e-6:
                    Shape(new[] { Point(line.StartPoint), Point(line.EndPoint) });
                    break;
                case Arc arc when arc.Normal.Z > .999:
                    Shape(ArcPoints(Point(arc.Center), arc.Radius, arc.StartAngle,
                        Sweep(arc.StartAngle, arc.EndAngle), true));
                    break;
                case Circle circle when circle.Normal.Z > .999:
                    Shape(ArcPoints(Point(circle.Center), circle.Radius, 0, 2 * Math.PI, false), true);
                    break;
                case LwPolyline poly when poly.Normal.Z > .999:
                    Shape(Polyline(poly.Vertices.Select(v => (new Point2(v.Location.X, v.Location.Y), v.Bulge)).ToArray(),
                        poly.IsClosed), poly.IsClosed);
                    break;
                case MText m when m.Normal.Z > .999:
                    Text(m.Value, Point(m.InsertPoint), m.Rotation, m.Height, ((int)m.AttachmentPoint - 1) % 3);
                    break;
                case TextEntity t when t.Normal.Z > .999:
                    int align = (int)t.HorizontalAlignment;
                    Text(t.Value, Point(align == 1 || align == 2 ? t.AlignmentPoint : t.InsertPoint),
                        t.Rotation, t.Height, align);
                    break;
                case Hatch hatch when hatch.Normal.Z > .999:
                    ReadHatch(hatch, transform, id, layer);
                    break;
                default:
                    Skip(entity.ObjectName);
                    break;
            }
        }

        private void ReadHatch(Hatch hatch, Func<Point2, Point2> transform, string id, string layer)
        {
            var loops = new List<Point2[]>();
            foreach (var path in hatch.Paths)
            {
                var points = new List<Point2>();
                foreach (var edge in path.Edges)
                {
                    IEnumerable<Point2> part;
                    switch (edge)
                    {
                        case Hatch.BoundaryPath.Line line:
                            part = new[] { new Point2(line.Start.X, line.Start.Y), new Point2(line.End.X, line.End.Y) };
                            break;
                        case Hatch.BoundaryPath.Arc arc:
                            double sweep = arc.CounterClockWise ? Sweep(arc.StartAngle, arc.EndAngle)
                                : -Sweep(arc.EndAngle, arc.StartAngle);
                            part = ArcPoints(new Point2(arc.Center.X, arc.Center.Y), arc.Radius, arc.StartAngle, sweep, true);
                            break;
                        case Hatch.BoundaryPath.Polyline poly:
                            part = Polyline(poly.Vertices.Select(v => (new Point2(v.X, v.Y), v.Z)).ToArray(), poly.IsClosed);
                            break;
                        default:
                            Skip("граница HATCH: " + edge.Type); return;
                    }
                    foreach (var p in part)
                        if (points.Count == 0 || points.Last().DistanceTo(p) > 1e-7) points.Add(p);
                }
                if (points.Count > 1 && points.First().DistanceTo(points.Last()) < 1e-7)
                    points.RemoveAt(points.Count - 1);
                if (points.Count >= 3) loops.Add(points.Select(transform).ToArray());
            }
            var sorted = loops.OrderByDescending(Recognition.Area).ToArray();
            var containers = sorted.Select((loop, i) => sorted.Take(i)
                .Where(parent => Recognition.Contains(parent, loop[0])).ToArray()).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                if (containers[i].Length % 2 != 0) continue;
                var outer = sorted[i];
                Add(new CadItem { Id = id + "/" + i, Layer = layer, Role = Role.Hatch, Closed = true, Points = outer,
                    Holes = sorted.Where((loop, j) => containers[j].LastOrDefault() == outer).ToArray() });
            }
        }

        private static string CleanText(string value)
        {
            value = Regex.Replace(value ?? "", @"\\[AaCcFfHhQqTtWw][^;]*;", "");
            value = Regex.Replace(value, @"\\[LlOoKk]", "");
            return value.Replace(@"\P", "\n").Replace(@"\~", " ").Replace("{", "").Replace("}", "")
                .Replace("%%d", "°").Replace("%%c", "Ø").Replace("%%p", "±").Trim();
        }

        private static Point2 Point(XYZ p) => new Point2(p.X, p.Y);

        private static IEnumerable<Point2> Polyline((Point2 P, double Bulge)[] vertices, bool closed)
        {
            if (vertices.Length == 0) yield break;
            yield return vertices[0].P;
            int count = closed ? vertices.Length : vertices.Length - 1;
            for (int i = 0; i < count; i++)
            {
                var start = vertices[i];
                var end = vertices[(i + 1) % vertices.Length].P;
                var delta = end - start.P;
                if (delta.Length < 1e-8) continue;
                if (Math.Abs(start.Bulge) > 1e-8)
                {
                    var center = (start.P + end) * .5 + new Point2(-delta.Y, delta.X) *
                        ((1 - start.Bulge * start.Bulge) / (4 * start.Bulge));
                    var points = ArcPoints(center, center.DistanceTo(start.P),
                        Math.Atan2(start.P.Y - center.Y, start.P.X - center.X),
                        4 * Math.Atan(start.Bulge), true).ToArray();
                    foreach (var point in points.Skip(1).Take(points.Length - 2))
                        yield return point;
                }
                if (i + 1 < vertices.Length) yield return end;
            }
        }

        private static double Sweep(double start, double end)
        {
            double sweep = (end - start) % (2 * Math.PI);
            return sweep > 0 ? sweep : sweep + 2 * Math.PI;
        }

        private static IEnumerable<Point2> ArcPoints(Point2 center, double radius, double start, double sweep, bool last)
        {
            int count = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 36)));
            for (int i = 0; i < count + (last ? 1 : 0); i++)
            {
                double angle = start + sweep * i / count;
                yield return center + new Point2(Math.Cos(angle), Math.Sin(angle)) * radius;
            }
        }

        private void Add(CadItem item)
        {
            if (item.Points.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y)))
            { Skip("некорректные координаты"); return; }
            if (item.Points.Length >= (item.Text != null ? 1 : item.Closed ? 3 : 2))
                drawing.Items.Add(item);
        }

        private void Skip(string reason)
        {
            drawing.Skipped.TryGetValue(reason, out int count);
            drawing.Skipped[reason] = count + 1;
        }
    }
}


