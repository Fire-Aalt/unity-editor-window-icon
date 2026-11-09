#if UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace BiteMe.EditorWindowIcon
{
    static class ProjectIcon
    {
        public static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static Texture2D GetDefault()
        {
            var icons = PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any);
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }
    }

    /// <summary>Straight-alpha RGBA pixels, top row first.</summary>
    sealed class IconPixels
    {
        public readonly int Width, Height;
        public readonly Color32[] Data;

        public IconPixels(int width, int height)
        {
            Width = width;
            Height = height;
            Data = new Color32[width * height];
        }

        /// <summary>Works on any texture, readable and compressed or not, by drawing it to a render texture.</summary>
        public static IconPixels FromTexture(Texture texture)
        {
            int w = texture.width, h = texture.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var active = RenderTexture.active;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var rows = copy.GetPixels32();
                var pixels = new IconPixels(w, h);
                for (int y = 0; y < h; y++)
                    Array.Copy(rows, (h - 1 - y) * w, pixels.Data, y * w, w);
                return pixels;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        /// <summary>Area-averaged resample (box filter) with premultiplied alpha, so edges stay clean.</summary>
        IconPixels Resize(int width, int height)
        {
            var result = new IconPixels(width, height);
            float sx = (float)Width / width, sy = (float)Height / height;
            for (int y = 0; y < height; y++)
            {
                float y0 = y * sy, y1 = (y + 1) * sy;
                for (int x = 0; x < width; x++)
                {
                    float x0 = x * sx, x1 = (x + 1) * sx;
                    float r = 0, g = 0, b = 0, a = 0, weight = 0;
                    for (int j = (int)y0; j < Mathf.Min(Height, Mathf.CeilToInt(y1)); j++)
                    {
                        float wy = Mathf.Min(y1, j + 1) - Mathf.Max(y0, j);
                        for (int i = (int)x0; i < Mathf.Min(Width, Mathf.CeilToInt(x1)); i++)
                        {
                            float w = wy * (Mathf.Min(x1, i + 1) - Mathf.Max(x0, i));
                            var c = Data[j * Width + i];
                            float ca = c.a * w;
                            r += c.r * ca;
                            g += c.g * ca;
                            b += c.b * ca;
                            a += ca;
                            weight += w;
                        }
                    }
                    result.Data[y * width + x] = a <= 0 ? default : new Color32(
                        (byte)Mathf.Round(r / a), (byte)Mathf.Round(g / a), (byte)Mathf.Round(b / a),
                        (byte)Mathf.Round(a / weight));
                }
            }
            return result;
        }

        /// <summary>Draws <paramref name="top"/> over these pixels with its top-left corner at (x, y).</summary>
        void Over(IconPixels top, int left, int upper)
        {
            for (int y = 0; y < top.Height; y++)
            for (int x = 0; x < top.Width; x++)
            {
                int tx = left + x, ty = upper + y;
                if (tx < 0 || ty < 0 || tx >= Width || ty >= Height)
                    continue;
                var src = top.Data[y * top.Width + x];
                ref var dst = ref Data[ty * Width + tx];
                float sa = src.a / 255f, da = dst.a / 255f * (1 - sa), outA = sa + da;
                if (outA <= 0)
                    continue;
                dst = new Color32(
                    (byte)Mathf.Round((src.r * sa + dst.r * da) / outA),
                    (byte)Mathf.Round((src.g * sa + dst.g * da) / outA),
                    (byte)Mathf.Round((src.b * sa + dst.b * da) / outA),
                    (byte)Mathf.Round(outA * 255));
            }
        }

        public IconPixels Badged(IconPixels badge, int size)
        {
            var icon = Resize(size, size);
            if (badge != null)
            {
                int badgeSize = Mathf.Max(1, Mathf.RoundToInt(size * 0.5f));
                icon.Over(badge.Resize(badgeSize, badgeSize), size - badgeSize, size - badgeSize);
            }
            return icon;
        }

        public byte[] ToPng()
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            try
            {
                var rows = new Color32[Data.Length];
                for (int y = 0; y < Height; y++)
                    Array.Copy(Data, y * Width, rows, (Height - 1 - y) * Width, Width);
                texture.SetPixels32(rows);
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
#endif
