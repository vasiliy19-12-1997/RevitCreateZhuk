using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace RevitCreateZhuk.Core
{
    public enum Role { Geometry, Axis, Dimension, Section, Text, Hatch, Facade, Title }

    public struct Point2
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2(double x, double y) { X = x; Y = y; }
        public static Point2 operator +(Point2 a, Point2 b) => new Point2(a.X + b.X, a.Y + b.Y);
        public static Point2 operator -(Point2 a, Point2 b) => new Point2(a.X - b.X, a.Y - b.Y);
        public static Point2 operator *(Point2 a, double n) => new Point2(a.X * n, a.Y * n);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public double DistanceTo(Point2 other) => (this - other).Length;
    }

    public sealed class CadItem
    {
        public string Id { get; set; }
        public string Layer { get; set; }
        public Role Role { get; set; }
        public Point2[] Points { get; set; }
        public Point2[][] Holes { get; set; }
        public bool Closed { get; set; }
        public bool Round { get; set; }
        public bool CenterText { get; set; }
        public Point2 TextOffsetMm { get; set; }
        public string Text { get; set; }
        public double Height { get; set; }
        public double Rotation { get; set; }
        public int Alignment { get; set; }
        public Bounds Bounds => Bounds.From(Points);

        internal CadItem WithPoints(Point2[] points)
        {
            var copy = (CadItem)MemberwiseClone();
            copy.Points = points;
            return copy;
        }

        internal CadItem WithText(string value, Point2 point, double height, double rotation, int alignment)
        {
            var copy = WithPoints(new[] { point });
            copy.Text = value;
            copy.Height = height;
            copy.Rotation = rotation;
            copy.Alignment = alignment;
            return copy;
        }
    }

    public sealed class Drawing
    {
        public double MillimetersPerUnit { get; set; }
        public List<CadItem> Items { get; set; } = new List<CadItem>();
        public Dictionary<string, int> Skipped { get; set; } = new Dictionary<string, int>();

        public static Drawing Read(Stream input) =>
            (Drawing)new DataContractJsonSerializer(typeof(Drawing)).ReadObject(input);

        public void Write(Stream output) =>
            new DataContractJsonSerializer(typeof(Drawing)).WriteObject(output, this);
    }

    public struct Bounds
    {
        public double Left, Bottom, Right, Top;
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public Point2 Center => new Point2((Left + Right) / 2, (Bottom + Top) / 2);

        public static Bounds From(IEnumerable<Point2> points) => new Bounds
        {
            Left = points.Min(p => p.X), Right = points.Max(p => p.X),
            Bottom = points.Min(p => p.Y), Top = points.Max(p => p.Y)
        };

        public Bounds Expand(double amount) => new Bounds
        {
            Left = Left - amount, Right = Right + amount, Bottom = Bottom - amount, Top = Top + amount
        };

        public bool Intersects(Bounds other) =>
            Left <= other.Right && Right >= other.Left && Bottom <= other.Top && Top >= other.Bottom;
    }
}
