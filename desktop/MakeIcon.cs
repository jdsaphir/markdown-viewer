// Build-time helper: draws the same mark as assets/favicon.svg and writes a
// multi-resolution .ico. Kept as source so the icon can be regenerated without
// any image tooling installed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    // Traced from the favicon's SVG path, in its 32x32 coordinate space.
    private static readonly float[] GlyphM =
    {
        6.5f,22f, 6.5f,10f, 9.7f,10f, 13f,15f, 16.3f,10f, 19.5f,10f,
        19.5f,22f, 16.3f,22f, 16.3f,15.3f, 13f,20.2f, 9.7f,15.3f, 9.7f,22f
    };

    private static readonly float[] GlyphArrow =
    {
        23.7f,22f, 19.3f,16.4f, 22.1f,16.4f, 22.1f,10f,
        25.3f,10f, 25.3f,16.4f, 28.1f,16.4f
    };

    private static readonly int[] Sizes = { 256, 128, 64, 48, 32, 24, 16 };

    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "MarkdownViewer.ico";
        var images = new List<byte[]>();

        foreach (int size in Sizes)
            images.Add(RenderPng(size));

        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0);            // reserved
            w.Write((short)1);            // type: icon
            w.Write((short)Sizes.Length);

            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);         // palette size
                w.Write((byte)0);         // reserved
                w.Write((short)1);        // colour planes
                w.Write((short)32);       // bits per pixel
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] png in images) w.Write(png);
        }

        Console.WriteLine("Wrote " + output + " (" + Sizes.Length + " sizes)");
        return 0;
    }

    private static byte[] RenderPng(int size)
    {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                float k = size / 32f;

                using (var bg = RoundedRect(0.4f * k, 0.4f * k, 31.2f * k, 31.2f * k, 7f * k))
                using (var brush = new SolidBrush(Color.FromArgb(0xC1, 0x5F, 0x3C)))
                    g.FillPath(brush, bg);

                using (var glyph = new GraphicsPath())
                {
                    glyph.AddPolygon(Scale(GlyphM, k));
                    glyph.AddPolygon(Scale(GlyphArrow, k));
                    using (var white = new SolidBrush(Color.White))
                        g.FillPath(white, glyph);
                }
            }

            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    private static PointF[] Scale(float[] flat, float k)
    {
        var pts = new PointF[flat.Length / 2];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF(flat[i * 2] * k, flat[i * 2 + 1] * k);
        return pts;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
