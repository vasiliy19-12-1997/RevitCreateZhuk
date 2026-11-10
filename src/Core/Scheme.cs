using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitCreateZhuk.Core
{
    internal sealed class Building
    {
        public string Name { get; set; }
        public CadItem Label { get; set; }
        public CadItem Outline { get; set; }
        public bool Selected { get; set; } = true;
    }

    internal sealed class Zone
    {
        public Bounds Bounds { get; set; }
        public List<CadItem> Items { get; } = new List<CadItem>();
        public List<CadItem> Outlines { get; } = new List<CadItem>();
        public List<Building> Buildings { get; } = new List<Building>();
    }

    internal sealed class SchemePart
    {
        public SchemePart(Role role, string[] buildings)
        {
            Role = role;
            Buildings = buildings;
        }

        public Role Role { get; }
        public Point2 Origin { get; set; }
        public double UnitFactor { get; set; } = 1;
        public string Mark { get; set; }
        public string[] Buildings { get; }
        public List<CadItem> Items { get; } = new List<CadItem>();

        public SchemePart WithItems(IEnumerable<CadItem> items)
        {
            var part = new SchemePart(Role, Buildings) { Origin = Origin, UnitFactor = UnitFactor, Mark = Mark };
            part.Items.AddRange(items);
            return part;
        }

        public Point2 TextAnchor(CadItem item, double primaryFeetPerUnit) =>
            (item.Points[0] - Origin) * (primaryFeetPerUnit * UnitFactor) + item.TextOffsetMm * (1 / 304.8);
    }

    internal static class Scheme
    {
        public static List<Zone> FindZones(Drawing drawing)
        {
            var outlines = drawing.Items.Where(i => i.Role == Role.Geometry && i.Points.Length >= 3 &&
                Recognition.Area(i.Points) > 0).ToList();
            var labels = drawing.Items.Where(i => i.Text != null)
                .Select(i => new Building { Name = Recognition.BuildingName(i.Text), Label = i })
                .Where(b => b.Name != null).ToList();
            var zones = new List<Zone>();
            while (outlines.Count > 0)
            {
                var zone = new Zone();
                zone.Outlines.Add(outlines[0]);
                outlines.RemoveAt(0);
                for (int k = 0; k < zone.Outlines.Count; k++)
                {
                    var box = zone.Outlines[k].Bounds.Expand(Math.Max(1e-8, zone.Outlines[k].Bounds.Width * .001));
                    for (int i = outlines.Count - 1; i >= 0; i--)
                    {
                        if (!box.Intersects(outlines[i].Bounds)) continue;
                        zone.Outlines.Add(outlines[i]);
                        outlines.RemoveAt(i);
                    }
                }
                zone.Bounds = Bounds.From(zone.Outlines.SelectMany(i => i.Points));
                zones.Add(zone);
            }
            if (zones.Count == 0 && drawing.Items.Count != 0)
                zones.Add(new Zone { Bounds = Bounds.From(drawing.Items.SelectMany(i => i.Points)) });

            foreach (var item in drawing.Items)
            {
                var box = item.Bounds;
                var zone = zones.OrderBy(z => BoxDistance(z.Bounds, box)).ThenBy(z => z.Bounds.Center.DistanceTo(box.Center))
                    .FirstOrDefault();
                if (zone == null) continue;
                bool buildingLabel = item.Text != null && Recognition.BuildingName(item.Text) != null;
                double padding = Math.Max(zone.Bounds.Width, zone.Bounds.Height) * .45;
                if (buildingLabel || zone.Bounds.Expand(padding).Intersects(box))
                    zone.Items.Add(item);
            }

            foreach (var zone in zones)
            {
                zone.Buildings.AddRange(labels.Where(b => zone.Items.Contains(b.Label)));
                AssignOutlines(zone);
            }
            return zones.OrderByDescending(z => z.Buildings.Count != 0)
                .ThenByDescending(z => z.Items.Count(i => i.Role == Role.Axis))
                .ThenBy(z => z.Bounds.Left).ToList();
        }

        private static double BoxDistance(Bounds a, Bounds b)
        {
            double x = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
            double y = Math.Max(0, Math.Max(a.Bottom - b.Top, b.Bottom - a.Top));
            return Math.Sqrt(x * x + y * y);
        }

        private static void AssignOutlines(Zone zone)
        {
            var available = zone.Outlines.ToList();
            var area = zone.Outlines.ToDictionary(i => i, i => Recognition.Area(i.Points));
            double maxArea = area.Values.DefaultIfEmpty(0).Max();
            var onlyBuilding = zone.Buildings.Count == 1 ? zone.Buildings[0] : null;
            if (onlyBuilding != null)
            {
                onlyBuilding.Outline = available.OrderByDescending(i => area[i]).FirstOrDefault();
                return;
            }

            double radius = Math.Max(zone.Bounds.Width, zone.Bounds.Height);
            var candidates = from building in zone.Buildings
                             from outline in available
                             let point = building.Label.Points[0]
                             let distance = Recognition.Contains(outline.Points, point) ? 0 : Recognition.Distance(outline, point)
                             let envelope = area[outline] > maxArea * .95 &&
                                 available.Count(o => area[o] < area[outline] * .5 && outline.Bounds.Intersects(o.Bounds)) >= 2
                             where distance <= radius * .24
                             orderby distance + (envelope ? radius * .22 : 0), area[outline]
                             select new { building, outline };
            foreach (var match in candidates.ToArray())
            {
                if (match.building.Outline == null && available.Remove(match.outline))
                    match.building.Outline = match.outline;
            }
        }

        public static List<SchemePart> Build(Zone zone, bool hatchSelected, bool overlayParking = false,
            ISet<string> parkingOverrides = null)
        {
            var parts = new Dictionary<string, SchemePart>(StringComparer.OrdinalIgnoreCase);
            bool Include(Building b) => b.Selected && (parkingOverrides == null || !parkingOverrides.Contains(b.Name));
            var fallback = zone.Buildings.Where(Include).ToArray();
            foreach (var item in zone.Items)
            {
                if (overlayParking && item.Role == Role.Geometry &&
                    !zone.Buildings.Any(b => item == b.Outline)) continue;
                var exact = zone.Buildings.Where(b => b.Label == item || b.Outline == item).ToArray();
                var owners = exact.Length != 0 ? exact : FindOwners(zone, item);
                var enabled = (owners.Length == 0 ? fallback : owners).Where(Include).ToArray();
                if (zone.Buildings.Count > 0 && enabled.Length == 0) continue;
                var building = exact.FirstOrDefault(b => b.Label == item);
                Add(building == null ? item : Composition.FormatBuilding(item, building.Outline), enabled);
                var points = item.Points;
                bool closes = item.Closed || (points.Length >= 4 && points[0].DistanceTo(points.Last()) < 1e-5);
                if (hatchSelected && item.Role == Role.Geometry && closes && exact.Length > 0 &&
                    !zone.Items.Any(h => h.Role == Role.Hatch && h.Bounds.Intersects(item.Bounds)))
                {
                    Add(new CadItem { Id = item.Id + "/fill", Role = Role.Hatch, Layer = item.Layer,
                        Closed = true, Points = item.Points }, enabled);
                }
            }
            foreach (var parking in fallback.Where(b => b.Label == null && b.Outline != null && Composition.IsParking(b.Name)))
            {
                var outline = parking.Outline;
                var label = new CadItem { Role = Role.Text, Text = parking.Name, Points = new[] { outline.Bounds.Center },
                    Height = Math.Max(zone.Bounds.Width, zone.Bounds.Height) * .03, Alignment = 1 };
                Add(Composition.FormatBuilding(label, outline), new[] { parking });
            }
            foreach (var axis in parts.Values.Where(p => p.Role == Role.Axis)) Composition.FormatAxes(axis);
            return parts.Values.OrderBy(p => p.Role == Role.Hatch ? 0 : 1).ToList();

            void Add(CadItem item, Building[] enabled)
            {
                var names = enabled.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                string key = ((int)item.Role) + "|" + string.Join("|", names);
                if (!parts.TryGetValue(key, out var part))
                {
                    part = new SchemePart(item.Role, names);
                    parts.Add(key, part);
                }
                part.Items.Add(item);
            }
        }

        private static Building[] FindOwners(Zone zone, CadItem item)
        {
            if (item.Role == Role.Axis || item.Role == Role.Dimension || item.Role == Role.Section)
                return Array.Empty<Building>();
            var matching = zone.Buildings.Where(b => b.Outline != null &&
                item.Points.All(p => Recognition.Distance(b.Outline, p) <= 1e-6 ||
                    Recognition.Contains(b.Outline.Points, p))).ToArray();
            return matching;
        }
    }
}

