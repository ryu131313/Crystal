using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ModernIcons
{
    /// <summary>
    /// SVG path data ("d") → GDI+ <see cref="GraphicsPath"/>. Supports M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z, implicit
    /// repeated commands (pairs after M are L), compact numbers (".5", "-1.5.5", "1e-3") and compact arc flags
    /// ("a.8.8 0 011.6 0"). Quadratics become cubics, endpoint arcs become ≤ 90° cubic segments.
    /// Subpaths with no visible extent (e.g. "M7 11h.01", "M18 10h0") are returned as <see cref="Dots"/>: SVG draws them
    /// as a round-cap dot, GDI+ may draw nothing, so the renderer fills a circle of the stroke width there instead.
    /// </summary>
    public sealed class SvgPath
    {
        public readonly GraphicsPath Path = new GraphicsPath(FillMode.Winding);
        public readonly List<PointF> Dots = new List<PointF>();

        private readonly string _d;
        private int _pos;

        // current figure, buffered so degenerate figures can become dots
        private readonly List<PointF[]> _segs = new List<PointF[]>(); // 2 points = line, 4 = cubic
        private bool _figClosed;

        private SvgPath(string d) { _d = d; }

        public static SvgPath Parse(string d)
        {
            var p = new SvgPath(d);
            p.Run();
            return p;
        }

        private void Run()
        {
            float cx = 0, cy = 0, sx = 0, sy = 0;
            float lcx = 0, lcy = 0; // last cubic control (for S)
            float lqx = 0, lqy = 0; // last quadratic control (for T)
            char prev = ' ';
            char cmd = ' ';

            SkipSep();
            while (_pos < _d.Length)
            {
                char c = _d[_pos];
                if (char.IsLetter(c))
                {
                    cmd = c;
                    _pos++;
                }
                else if (cmd == ' ')
                    throw new FormatException($"path data must start with a command at {_pos}");
                else if (cmd == 'M') cmd = 'L'; // implicit lineto after moveto
                else if (cmd == 'm') cmd = 'l';
                else if (cmd == 'Z' || cmd == 'z')
                    throw new FormatException($"number after Z at {_pos}");

                bool rel = char.IsLower(cmd);
                float ox = rel ? cx : 0, oy = rel ? cy : 0;

                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        FlushFigure();
                        cx = ox + Num(); cy = oy + Num();
                        sx = cx; sy = cy;
                        _figStart = new PointF(cx, cy);
                        break;
                    case 'L':
                    {
                        float x = ox + Num(), y = oy + Num();
                        Line(cx, cy, x, y); cx = x; cy = y;
                        break;
                    }
                    case 'H':
                    {
                        float x = ox + Num();
                        Line(cx, cy, x, cy); cx = x;
                        break;
                    }
                    case 'V':
                    {
                        float y = oy + Num();
                        Line(cx, cy, cx, y); cy = y;
                        break;
                    }
                    case 'C':
                    {
                        float x1 = ox + Num(), y1 = oy + Num(), x2 = ox + Num(), y2 = oy + Num(), x = ox + Num(), y = oy + Num();
                        Cubic(cx, cy, x1, y1, x2, y2, x, y);
                        lcx = x2; lcy = y2; cx = x; cy = y;
                        break;
                    }
                    case 'S':
                    {
                        float x1 = cx, y1 = cy;
                        char p = char.ToUpperInvariant(prev);
                        if (p == 'C' || p == 'S') { x1 = 2 * cx - lcx; y1 = 2 * cy - lcy; }
                        float x2 = ox + Num(), y2 = oy + Num(), x = ox + Num(), y = oy + Num();
                        Cubic(cx, cy, x1, y1, x2, y2, x, y);
                        lcx = x2; lcy = y2; cx = x; cy = y;
                        break;
                    }
                    case 'Q':
                    {
                        float qx = ox + Num(), qy = oy + Num(), x = ox + Num(), y = oy + Num();
                        Quad(cx, cy, qx, qy, x, y);
                        lqx = qx; lqy = qy; cx = x; cy = y;
                        break;
                    }
                    case 'T':
                    {
                        float qx = cx, qy = cy;
                        char p = char.ToUpperInvariant(prev);
                        if (p == 'Q' || p == 'T') { qx = 2 * cx - lqx; qy = 2 * cy - lqy; }
                        float x = ox + Num(), y = oy + Num();
                        Quad(cx, cy, qx, qy, x, y);
                        lqx = qx; lqy = qy; cx = x; cy = y;
                        break;
                    }
                    case 'A':
                    {
                        float rx = Num(), ry = Num(), rot = Num();
                        bool large = Flag(), sweep = Flag();
                        float x = ox + Num(), y = oy + Num();
                        Arc(cx, cy, rx, ry, rot, large, sweep, x, y);
                        cx = x; cy = y;
                        break;
                    }
                    case 'Z':
                        _figClosed = true;
                        FlushFigure();
                        cx = sx; cy = sy;
                        _figStart = new PointF(cx, cy);
                        break;
                    default:
                        throw new FormatException($"unknown command '{cmd}' at {_pos - 1}");
                }
                prev = cmd;
                SkipSep();
            }
            FlushFigure();
        }

        private PointF _figStart;

        private void Line(float x0, float y0, float x1, float y1)
            => _segs.Add(new[] { new PointF(x0, y0), new PointF(x1, y1) });

        private void Cubic(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
            => _segs.Add(new[] { new PointF(x0, y0), new PointF(x1, y1), new PointF(x2, y2), new PointF(x3, y3) });

        private void Quad(float x0, float y0, float qx, float qy, float x, float y)
            => Cubic(x0, y0, x0 + 2f / 3f * (qx - x0), y0 + 2f / 3f * (qy - y0), x + 2f / 3f * (qx - x), y + 2f / 3f * (qy - y), x, y);

        /// <summary>SVG endpoint arc → centre parameterisation (SVG 1.1 F.6.5/F.6.6) → cubic segments of ≤ 90°.</summary>
        private void Arc(float x1, float y1, float rx, float ry, float rotDeg, bool large, bool sweep, float x2, float y2)
        {
            if (x1 == x2 && y1 == y2) return;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx == 0 || ry == 0) { Line(x1, y1, x2, y2); return; }

            double phi = rotDeg * Math.PI / 180.0, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
            double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;

            double rxs = (double)rx * rx, rys = (double)ry * ry;
            double lambda = x1p * x1p / rxs + y1p * y1p / rys;
            double Rx = rx, Ry = ry;
            if (lambda > 1) { double s = Math.Sqrt(lambda); Rx *= s; Ry *= s; rxs = Rx * Rx; rys = Ry * Ry; }

            double num = rxs * rys - rxs * y1p * y1p - rys * x1p * x1p;
            double den = rxs * y1p * y1p + rys * x1p * x1p;
            double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (large == sweep) coef = -coef;
            double cxp = coef * (Rx * y1p / Ry), cyp = coef * (-Ry * x1p / Rx);
            double ccx = cos * cxp - sin * cyp + (x1 + x2) / 2.0;
            double ccy = sin * cxp + cos * cyp + (y1 + y2) / 2.0;

            double ux = (x1p - cxp) / Rx, uy = (y1p - cyp) / Ry;
            double vx = (-x1p - cxp) / Rx, vy = (-y1p - cyp) / Ry;
            double theta1 = Math.Atan2(uy, ux);
            double dtheta = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            if (!sweep && dtheta > 0) dtheta -= 2 * Math.PI;
            else if (sweep && dtheta < 0) dtheta += 2 * Math.PI;

            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dtheta) / (Math.PI / 2) - 1e-9));
            double step = dtheta / n;
            double k = 4.0 / 3.0 * Math.Tan(step / 4);
            double t = theta1;
            PointF P(double a, double b) => new PointF((float)(ccx + cos * Rx * a - sin * Ry * b), (float)(ccy + sin * Rx * a + cos * Ry * b));
            PointF start = new PointF(x1, y1);
            for (int i = 0; i < n; i++)
            {
                double t2 = t + step;
                double c1 = Math.Cos(t), s1 = Math.Sin(t), c2 = Math.Cos(t2), s2 = Math.Sin(t2);
                PointF p1 = P(c1 - k * s1, s1 + k * c1);
                PointF p2 = P(c2 + k * s2, s2 - k * c2);
                PointF p3 = i == n - 1 ? new PointF(x2, y2) : P(c2, s2);
                _segs.Add(new[] { start, p1, p2, p3 });
                start = p3;
                t = t2;
            }
        }

        private void FlushFigure()
        {
            if (_segs.Count == 0) { _figClosed = false; return; }

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var s in _segs)
                foreach (var p in s)
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }

            if (maxX - minX < 0.05f && maxY - minY < 0.05f)
            {
                Dots.Add(new PointF((minX + maxX) / 2, (minY + maxY) / 2));
            }
            else
            {
                Path.StartFigure();
                foreach (var s in _segs)
                {
                    if (s.Length == 2) Path.AddLine(s[0], s[1]);
                    else Path.AddBezier(s[0], s[1], s[2], s[3]);
                }
                if (_figClosed) Path.CloseFigure();
            }
            _segs.Clear();
            _figClosed = false;
        }

        private void SkipSep()
        {
            while (_pos < _d.Length && (char.IsWhiteSpace(_d[_pos]) || _d[_pos] == ',')) _pos++;
        }

        private bool Flag()
        {
            SkipSep();
            if (_pos >= _d.Length || (_d[_pos] != '0' && _d[_pos] != '1'))
                throw new FormatException($"expected arc flag at {_pos}");
            return _d[_pos++] == '1';
        }

        private float Num()
        {
            SkipSep();
            int start = _pos;
            if (_pos < _d.Length && (_d[_pos] == '+' || _d[_pos] == '-')) _pos++;
            bool digits = false, dot = false;
            while (_pos < _d.Length)
            {
                char c = _d[_pos];
                if (c >= '0' && c <= '9') { digits = true; _pos++; }
                else if (c == '.' && !dot) { dot = true; _pos++; }
                else break;
            }
            if (!digits) throw new FormatException($"expected number at {start} in \"{_d}\"");
            if (_pos < _d.Length && (_d[_pos] == 'e' || _d[_pos] == 'E'))
            {
                int save = _pos;
                _pos++;
                if (_pos < _d.Length && (_d[_pos] == '+' || _d[_pos] == '-')) _pos++;
                int expStart = _pos;
                while (_pos < _d.Length && _d[_pos] >= '0' && _d[_pos] <= '9') _pos++;
                if (_pos == expStart) _pos = save; // not an exponent
            }
            return float.Parse(_d.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
