using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace GameColorGuard.Core;

/// <summary>
/// Takım logoları için küçük SVG çizici: path, polygon, polyline, rect, circle, ellipse; dolgu rengi (öznitelik, style,
/// CSS sınıfı, gradyanın ilk rengi), opaklık ve transform. Desteklenmeyen bir öğe görülürse null döner ve arayüz
/// yedek rozete düşer (yarım/yanlış logo çizilmez).
/// </summary>
internal static class SvgLite
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HashSet<string> Ignored = new() { "title", "desc", "metadata", "style", "defs", "linearGradient", "radialGradient", "stop" };

    public static bool LooksLikeSvg(byte[] bytes)
    {
        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
               (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    public static ImageSource? Render(byte[] bytes)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            XElement root = XDocument.Load(reader).Root!;
            XNamespace ns = root.Name.Namespace;

            Rect view = ViewBox(root);
            var classes = ParseCss(string.Concat(root.Descendants(ns + "style").Select(s => s.Value)));
            var gradients = root.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient")
                .Where(e => e.Attribute("id") is not null)
                .ToDictionary(e => e.Attribute("id")!.Value, e => FirstStop(e, root));

            var group = new DrawingGroup();
            // Görüntü kutusu kadar şeffaf zemin: logonun oranı ve kenar boşluğu korunur.
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(view)));
            var state = new Style(Colors.Black, true, 1, 1, Matrix.Identity, FillRule.Nonzero);
            if (!Walk(root, state, classes, gradients, group)) return null;

            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private sealed record Style(Color Fill, bool HasFill, double FillOpacity, double Opacity, Matrix Transform, FillRule Rule);

    private static bool Walk(XElement element, Style parent, Dictionary<string, Dictionary<string, string>> classes,
        Dictionary<string, Color?> gradients, DrawingGroup output)
    {
        foreach (XElement child in element.Elements())
        {
            string name = child.Name.LocalName;
            if (Ignored.Contains(name)) continue;
            if (name is "use" or "image" or "text" or "mask" or "clipPath" or "pattern" or "filter" or "foreignObject") return false;

            Style style = Apply(child, parent, classes, gradients);
            if (name is "g" or "svg")
            {
                if (!Walk(child, style, classes, gradients, output)) return false;
                continue;
            }

            Geometry? geometry = Shape(child, name);
            if (geometry is null) return false;
            if (!style.HasFill || geometry.IsEmpty()) continue;
            if (geometry is PathGeometry pathGeometry) pathGeometry.FillRule = style.Rule;
            else if (geometry is StreamGeometry streamGeometry) streamGeometry.FillRule = style.Rule;
            if (!style.Transform.IsIdentity) geometry.Transform = new MatrixTransform(style.Transform);
            var brush = new SolidColorBrush(style.Fill) { Opacity = Math.Clamp(style.FillOpacity * style.Opacity, 0, 1) };
            output.Children.Add(new GeometryDrawing(brush, null, geometry));
        }
        return true;
    }

    private static Geometry? Shape(XElement e, string name)
    {
        double A(string attr) => double.TryParse(e.Attribute(attr)?.Value?.Replace("px", ""), NumberStyles.Float, Inv, out double v) ? v : 0;
        switch (name)
        {
            case "path":
                string? d = e.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(d)) return Geometry.Empty;
                return PathGeometry.CreateFromGeometry(Geometry.Parse(NormalizePath(d)));
            case "polygon":
            case "polyline":
                double[] points = Numbers(e.Attribute("points")?.Value ?? "");
                if (points.Length < 4) return Geometry.Empty;
                var builder = new StringBuilder("M");
                for (int i = 0; i + 1 < points.Length; i += 2) builder.Append(' ').Append(points[i].ToString(Inv)).Append(',').Append(points[i + 1].ToString(Inv));
                if (name == "polygon") builder.Append(" Z");
                return PathGeometry.CreateFromGeometry(Geometry.Parse(builder.ToString()));
            case "rect":
                return new RectangleGeometry(new Rect(A("x"), A("y"), A("width"), A("height")), A("rx"), A("ry") == 0 ? A("rx") : A("ry"));
            case "circle":
                return new EllipseGeometry(new Point(A("cx"), A("cy")), A("r"), A("r"));
            case "ellipse":
                return new EllipseGeometry(new Point(A("cx"), A("cy")), A("rx"), A("ry"));
            case "line":
                return Geometry.Empty;   // dolgusu olmayan çizgi: logoda görünmez
            default:
                return null;
        }
    }

    /// <summary>SVG yol verisini WPF'nin anlayacağı biçime getirir (ör. "1.5.5" iki sayıdır, yay bayrakları bitişik yazılabilir).</summary>
    private static string NormalizePath(string d)
    {
        // Bitişik sayıları ayır: ".5.5" → ".5 .5" ve "1-2" → "1 -2"
        string spaced = Regex.Replace(d, @"(\.\d+)(?=\.)", "$1 ");
        spaced = Regex.Replace(spaced, @"(?<=[0-9.])-", " -");
        // Yay komutlarında bitişik bayraklar ("a1 1 0 011 1"): bayrakları ayrı yaz.
        spaced = Regex.Replace(spaced, @"([aA])\s*([^a-zA-Z]+)", m => m.Groups[1].Value + " " + SplitArcFlags(m.Groups[2].Value));
        return spaced;
    }

    private static string SplitArcFlags(string args)
    {
        var tokens = new List<string>();
        var matches = Regex.Matches(args, @"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
        int index = 0;
        foreach (Match m in matches)
        {
            int slot = index % 7;
            // 4. ve 5. değerler (large-arc, sweep) tek basamaklı bayraklardır: "011" → "0 1 1"
            if ((slot == 3 || slot == 4) && m.Value.Length > 1 && m.Value.All(c => c is '0' or '1'))
            {
                foreach (char c in m.Value) { tokens.Add(c.ToString()); index++; }
                continue;
            }
            tokens.Add(m.Value);
            index++;
        }
        return string.Join(" ", tokens);
    }

    private static Style Apply(XElement e, Style parent, Dictionary<string, Dictionary<string, string>> classes, Dictionary<string, Color?> gradients)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cls in (e.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (classes.TryGetValue(cls, out var css)) foreach (var p in css) props[p.Key] = p.Value;
        foreach (string attr in new[] { "fill", "fill-opacity", "opacity", "fill-rule" })
            if (e.Attribute(attr)?.Value is string v) props[attr] = v;
        foreach (string declaration in (e.Attribute("style")?.Value ?? "").Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon > 0) props[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
        }

        Color fill = parent.Fill;
        bool hasFill = parent.HasFill;
        if (props.TryGetValue("fill", out string? f))
        {
            if (f == "none" || f == "transparent") hasFill = false;
            else if (f.StartsWith("url(#") && gradients.TryGetValue(f[5..^1], out Color? g) && g is Color gc) { fill = gc; hasFill = true; }
            else if (ParseColor(f) is Color c) { fill = c; hasFill = true; }
        }
        double fillOpacity = props.TryGetValue("fill-opacity", out string? fo) && double.TryParse(fo, NumberStyles.Float, Inv, out double fov) ? fov : parent.FillOpacity;
        double opacity = parent.Opacity * (props.TryGetValue("opacity", out string? o) && double.TryParse(o, NumberStyles.Float, Inv, out double ov) ? ov : 1);
        FillRule rule = props.TryGetValue("fill-rule", out string? fr) ? (fr == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero) : parent.Rule;
        Matrix transform = parent.Transform;
        if (e.Attribute("transform")?.Value is string t) transform = Matrix.Multiply(ParseTransform(t), transform);
        return new Style(fill, hasFill, fillOpacity, opacity, transform, rule);
    }

    private static Matrix ParseTransform(string value)
    {
        var result = Matrix.Identity;
        foreach (Match m in Regex.Matches(value, @"(matrix|translate|scale|rotate)\s*\(([^)]*)\)"))
        {
            double[] n = Numbers(m.Groups[2].Value);
            var step = Matrix.Identity;
            switch (m.Groups[1].Value)
            {
                case "matrix" when n.Length == 6: step = new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]); break;
                case "translate": step.Translate(n.ElementAtOrDefault(0), n.ElementAtOrDefault(1)); break;
                case "scale": step.Scale(n.ElementAtOrDefault(0), n.Length > 1 ? n[1] : n.ElementAtOrDefault(0)); break;
                case "rotate":
                    if (n.Length >= 3) step.RotateAt(n[0], n[1], n[2]); else step.Rotate(n.ElementAtOrDefault(0));
                    break;
            }
            // SVG'de soldaki dönüşüm en son uygulanır.
            result = Matrix.Multiply(step, result);
        }
        return result;
    }

    private static Rect ViewBox(XElement root)
    {
        double[] box = Numbers(root.Attribute("viewBox")?.Value ?? "");
        if (box.Length == 4 && box[2] > 0 && box[3] > 0) return new Rect(box[0], box[1], box[2], box[3]);
        double width = Numbers(root.Attribute("width")?.Value ?? "").FirstOrDefault();
        double height = Numbers(root.Attribute("height")?.Value ?? "").FirstOrDefault();
        return new Rect(0, 0, width > 0 ? width : 100, height > 0 ? height : 100);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseCss(string css)
    {
        var map = new Dictionary<string, Dictionary<string, string>>();
        foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^}]*)\}"))
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string declaration in rule.Groups[2].Value.Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon > 0) props[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
            foreach (string selector in rule.Groups[1].Value.Split(','))
            {
                string s = selector.Trim();
                if (!s.StartsWith('.')) continue;
                if (!map.TryGetValue(s[1..], out var existing)) map[s[1..]] = existing = new(StringComparer.OrdinalIgnoreCase);
                foreach (var p in props) existing[p.Key] = p.Value;
            }
        }
        return map;
    }

    private static Color? FirstStop(XElement gradient, XElement root)
    {
        XElement? stop = gradient.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
        if (stop is null && gradient.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))?.Value is string href && href.StartsWith('#'))
            stop = root.Descendants().FirstOrDefault(e => e.Attribute("id")?.Value == href[1..])?.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
        if (stop is null) return null;
        string? color = stop.Attribute("stop-color")?.Value;
        if (color is null && stop.Attribute("style")?.Value is string style)
            color = Regex.Match(style, @"stop-color\s*:\s*([^;]+)").Groups[1].Value;
        return color is null ? null : ParseColor(color);
    }

    private static Color? ParseColor(string value)
    {
        value = value.Trim();
        Match rgb = Regex.Match(value, @"^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)");
        if (rgb.Success)
            return Color.FromRgb((byte)double.Parse(rgb.Groups[1].Value, Inv), (byte)double.Parse(rgb.Groups[2].Value, Inv), (byte)double.Parse(rgb.Groups[3].Value, Inv));
        if (Regex.IsMatch(value, "^#[0-9a-fA-F]{3}$"))
            value = "#" + string.Concat(value[1..].Select(c => $"{c}{c}"));
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return null; }
    }

    private static double[] Numbers(string value) =>
        Regex.Matches(value, @"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, Inv)).ToArray();
}
