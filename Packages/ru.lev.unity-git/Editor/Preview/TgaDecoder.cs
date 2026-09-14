namespace Lev.Git.Preview
{
    /// <summary>
    /// TGA из байтов, без Unity. Unity сама TGA из памяти не читает, а формат
    /// простой: несжатый или RLE, цветной 24/32 бита или серый 8/16.
    /// С палитрой не поддерживается — в играх он почти не встречается.
    ///
    /// Результат — RGBA по 4 байта, строки снизу вверх: так их ждёт
    /// Texture2D.LoadRawTextureData.
    /// </summary>
    public static class TgaDecoder
    {
        public static bool TryDecode(byte[] data, out int width, out int height, out byte[] rgba, out string error)
        {
            width = height = 0;
            rgba = null;
            error = null;

            if (data == null || data.Length < 18) { error = L.T("file is shorter than the TGA header"); return false; }

            int idLength = data[0];
            int colorMapType = data[1];
            int imageType = data[2];
            int colorMapLength = data[5] | data[6] << 8;
            int colorMapDepth = data[7];
            width = data[12] | data[13] << 8;
            height = data[14] | data[15] << 8;
            int depth = data[16];
            int spec = data[17];

            bool rle = imageType == 10 || imageType == 11;
            bool gray = imageType == 3 || imageType == 11;

            if (imageType != 2 && imageType != 3 && imageType != 10 && imageType != 11)
            {
                error = L.T("this TGA type (color-mapped or without an image) is not supported");
                return false;
            }

            if (gray ? depth != 8 && depth != 16 : depth != 24 && depth != 32)
            {
                error = L.F("color depth of {0} bits is not supported", depth);
                return false;
            }

            if (width == 0 || height == 0 || (long)width * height > 16384L * 16384L)
            {
                error = L.F("size {0}×{1} is not valid", width, height);
                return false;
            }

            int bpp = depth / 8;
            int count = width * height;
            int src = 18 + idLength + (colorMapType == 1 ? colorMapLength * ((colorMapDepth + 7) / 8) : 0);
            var pixels = new byte[count * 4];

            if (!rle)
            {
                if ((long)src + (long)count * bpp > data.Length) { error = L.T("TGA file is truncated"); return false; }
                for (int i = 0; i < count; i++, src += bpp) Read(data, src, pixels, i * 4, bpp, gray);
            }
            else
            {
                int p = 0;
                while (p < count)
                {
                    if (src >= data.Length) { error = L.T("TGA file is truncated"); return false; }

                    int header = data[src++];
                    int n = (header & 0x7F) + 1;

                    if ((header & 0x80) != 0)
                    {
                        if (src + bpp > data.Length) { error = L.T("TGA file is truncated"); return false; }
                        for (int k = 0; k < n && p < count; k++, p++) Read(data, src, pixels, p * 4, bpp, gray);
                        src += bpp;
                    }
                    else
                    {
                        if ((long)src + (long)n * bpp > data.Length) { error = L.T("TGA file is truncated"); return false; }
                        for (int k = 0; k < n && p < count; k++, p++, src += bpp) Read(data, src, pixels, p * 4, bpp, gray);
                    }
                }
            }

            // В файле строки по умолчанию снизу вверх — как у Unity. Флаги
            // заголовка переворачивают порядок строк и пикселей в строке.
            bool topDown = (spec & 0x20) != 0;
            bool rightToLeft = (spec & 0x10) != 0;

            rgba = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                int row = topDown ? height - 1 - y : y;
                for (int x = 0; x < width; x++)
                {
                    int sx = rightToLeft ? width - 1 - x : x;
                    System.Buffer.BlockCopy(pixels, (row * width + sx) * 4, rgba, (y * width + x) * 4, 4);
                }
            }

            return true;
        }

        private static void Read(byte[] data, int src, byte[] pixels, int dst, int bpp, bool gray)
        {
            if (gray)
            {
                byte g = data[src];
                pixels[dst] = g;
                pixels[dst + 1] = g;
                pixels[dst + 2] = g;
                pixels[dst + 3] = bpp == 2 ? data[src + 1] : (byte)255;
                return;
            }

            pixels[dst] = data[src + 2];
            pixels[dst + 1] = data[src + 1];
            pixels[dst + 2] = data[src];
            pixels[dst + 3] = bpp == 4 ? data[src + 3] : (byte)255;
        }
    }
}
