using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitCreateZhuk.Core
{
    internal static class Recognition
    {
        private static readonly Regex NamePattern = new Regex(
            @"^(?:(?<name>(?:корпус|секция|пристройка)\s+[А-ЯA-Z0-9][А-ЯA-Z0-9.\-_/ ]*)|(?<name>паркинг|автостоянка)(?<level>[\s\S]*))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static Role LayerRole(string name)
        {
            if (Regex.IsMatch(name, @"(^|[\W_])(оси|ось|grid|axis|axes)([\W_]|$)", RegexOptions.IgnoreCase))
                return Role.Axis;
            if (Regex.IsMatch(name, @"размер|(^|[\W_])dim(s|ension)?([\W_]|$)", RegexOptions.IgnoreCase))
                return Role.Dimension;
            if (Regex.IsMatch(name, @"разрез|фасад|section|elev[-_ ]?mark", RegexOptions.IgnoreCase))
                return Role.Section;
            return Role.Geometry;
        }

        public static string BuildingName(string text)
        {
            var match = NamePattern.Match(text.Trim());
            if (!match.Success) return null;
            var name = match.Groups["name"].Value.Trim().TrimEnd('.');
            if (!match.Groups["level"].Success) return name;
            var level = Regex.Match(match.Groups["level"].Value, @"[−+-]?\d+");
            return level.Success ? name + " " + level.Value.Replace('−', '-') + " уровень" : name;
        }

        public static bool Contains(Point2[] polygon, Point2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i]; var b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        public static double Area(Point2[] points)
        {
            var origin = points[0];
            double area = 0;
            for (int i = 1; i < points.Length - 1; i++)
            {
                var a = points[i] - origin;
                var b = points[i + 1] - origin;
                area += a.X * b.Y - a.Y * b.X;
            }
            return Math.Abs(area) / 2;
        }

        public static double Distance(CadItem item, Point2 point)
        {
            if (item.Closed && Contains(item.Points, point)) return 0;
            var points = item.Points;
            double distance = points[0].DistanceTo(point);
            for (int i = 1; i < points.Length + (item.Closed ? 1 : 0); i++)
            {
                var start = points[i - 1]; var delta = points[i % points.Length] - start;
                double lengthSquared = delta.X * delta.X + delta.Y * delta.Y;
                if (lengthSquared == 0) continue;
                var offset = point - start;
                double t = Math.Max(0, Math.Min(1, (offset.X * delta.X + offset.Y * delta.Y) / lengthSquared));
                distance = Math.Min(distance, (start + delta * t).DistanceTo(point));
            }
            return distance;
        }
    }
}
